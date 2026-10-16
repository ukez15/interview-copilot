using System.Diagnostics.CodeAnalysis;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Application.Copilot;

/// <summary>
/// Answers interviewer questions without any typing (#22). Listens to saved transcript segments; for an
/// interviewer question in a live session it publishes <c>question.detected</c> and, unless auto-answer is
/// paused, streams an answer through <see cref="AnswerGenerator"/>. A newer question cancels the in-flight answer.
/// </summary>
public sealed partial class AutoAnswerService : ITranscriptSegmentListener, IDisposable
{
    /// <summary>Longest an automatic answer may run before it is stopped.</summary>
    public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Sent to clients when an automatic answer fails for a reason that isn't a user-facing provider error.</summary>
    public const string GenericFailureMessage = "The automatic answer couldn't be generated.";

    /// <summary>Sent to clients when an automatic answer runs past the time limit.</summary>
    public const string TimeoutMessage = "The answer took too long. Try a smaller model in Settings.";

    private readonly IServiceScopeFactory _scopes;
    private readonly ISessionEventPublisher _events;
    private readonly ILogger<AutoAnswerService> _logger;
    private readonly TimeSpan _answerTimeout;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InFlight> _inFlight = [];

    /// <summary>Creates the service.</summary>
    /// <param name="scopes">Creates a scope per answer for the scoped repositories and generator.</param>
    /// <param name="events">Where question and error events go.</param>
    /// <param name="logger">Logger; never receives transcript or answer text.</param>
    /// <param name="answerTimeout">Per-answer time limit; defaults to <see cref="DefaultAnswerTimeout"/>.</param>
    public AutoAnswerService(
        IServiceScopeFactory scopes,
        ISessionEventPublisher events,
        ILogger<AutoAnswerService> logger,
        TimeSpan? answerTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _events = events;
        _logger = logger;
        _answerTimeout = answerTimeout ?? DefaultAnswerTimeout;
    }

    /// <inheritdoc />
    public async ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Speaker != Speaker.Interviewer || !QuestionDetector.IsQuestion(segment.Text))
        {
            return;
        }

        bool autoAnswer;
        using (var scope = _scopes.CreateScope())
        {
            var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .GetAsync(segment.SessionId, cancellationToken);
            if (session is null || session.Status != SessionStatus.Live)
            {
                return;
            }

            autoAnswer = session.AutoAnswer;
        }

        // Publish first: clients reset their answer card on question.detected, so it must precede the answer's deltas.
        _events.Publish(segment.SessionId, SessionEventTypes.QuestionDetected, new { segmentId = segment.Id, text = segment.Text });

        // A newer question replaces whatever is still being answered, even when auto-answer is now paused.
        Replace(segment.SessionId, autoAnswer ? segment.Text : null);
    }

    /// <summary>Completes when the session's current automatic answer (if any) has finished or been cancelled.</summary>
    /// <param name="sessionId">The session to wait for.</param>
    /// <returns>A task that completes when no answer is running for the session.</returns>
    public async Task WhenIdleAsync(Guid sessionId)
    {
        while (true)
        {
            Task? running;
            lock (_gate)
            {
                running = _inFlight.TryGetValue(sessionId, out var current) ? current.Task : null;
            }

            if (running is null)
            {
                return;
            }

            await running;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _inFlight.Values)
            {
                entry.Cancel();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic answer failed for session {SessionId} ({ErrorType}).")]
    private partial void LogAnswerFailed(Guid sessionId, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic answer for session {SessionId} timed out.")]
    private partial void LogAnswerTimedOut(Guid sessionId);

    /// <summary>Stops the session's running answer and, when a question is given, starts answering it.</summary>
    private void Replace(Guid sessionId, string? question)
    {
        lock (_gate)
        {
            if (_inFlight.Remove(sessionId, out var previous))
            {
                previous.Cancel();
            }

            if (question is null)
            {
                return;
            }

            var entry = new InFlight(_answerTimeout);
            entry.Task = Task.Run(() => RunAsync(sessionId, question, entry), CancellationToken.None);
            _inFlight[sessionId] = entry;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed background answer is reported as an error event and must not crash the host.")]
    private async Task RunAsync(Guid sessionId, string question, InFlight self)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var generator = scope.ServiceProvider.GetRequiredService<AnswerGenerator>();
            var (_, tokens) = await generator.GenerateAsync(new AnswerRequest(sessionId, question), self.Token);
            await foreach (var token in tokens.WithCancellation(self.Token))
            {
                _ = token; // Consuming the stream is what publishes answer.delta / answer.done.
            }
        }
        catch (OperationCanceledException)
        {
            if (!self.CancelledByCaller)
            {
                LogAnswerTimedOut(sessionId);
                _events.Publish(sessionId, SessionEventTypes.Error, new { message = TimeoutMessage });
            }
        }
        catch (LlmUnavailableException ex)
        {
            _events.Publish(sessionId, SessionEventTypes.Error, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            LogAnswerFailed(sessionId, ex.GetType().Name);
            _events.Publish(sessionId, SessionEventTypes.Error, new { message = GenericFailureMessage });
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(sessionId, out var current) && ReferenceEquals(current, self))
                {
                    _inFlight.Remove(sessionId);
                }

                self.Dispose();
            }
        }
    }

    private sealed class InFlight(TimeSpan timeout) : IDisposable
    {
        private readonly CancellationTokenSource _source = new(timeout);

        public CancellationToken Token => _source.Token;

        public bool CancelledByCaller { get; private set; }

        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            CancelledByCaller = true;
            _source.Cancel();
        }

        public void Dispose() => _source.Dispose();
    }
}
