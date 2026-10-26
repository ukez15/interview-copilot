using System.Diagnostics.CodeAnalysis;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Application.Audio;

/// <summary>
/// Drains the queues filled by <see cref="TranscriptionSink"/>: cuts speech with <see cref="VoiceActivityBuffer"/>,
/// transcribes each segment, saves it and publishes it. A failed segment is reported and skipped.
/// </summary>
public sealed partial class TranscriptionWorker : BackgroundService
{
    /// <summary>Sent to clients when a segment fails for a reason that isn't a user-facing provider error.</summary>
    public const string GenericFailureMessage = "Part of the conversation couldn't be transcribed.";

    private readonly TranscriptionSink _sink;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISessionEventPublisher _publisher;
    private readonly ILogger<TranscriptionWorker> _logger;

    /// <summary>Creates the worker.</summary>
    /// <param name="sink">The sink whose queues are drained.</param>
    /// <param name="scopes">Creates a scope per segment for the scoped repositories.</param>
    /// <param name="publisher">Where transcript and error events go.</param>
    /// <param name="logger">Logger; never receives audio or transcript text.</param>
    public TranscriptionWorker(
        TranscriptionSink sink,
        IServiceScopeFactory scopes,
        ISessionEventPublisher publisher,
        ILogger<TranscriptionWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);
        _sink = sink;
        _scopes = scopes;
        _publisher = publisher;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drains = new List<Task>();
        try
        {
            await foreach (var pipeline in _sink.NewPipelines.ReadAllAsync(stoppingToken))
            {
                drains.Add(DrainAsync(pipeline, stoppingToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }

        await Task.WhenAll(drains);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} audio frames for session {SessionId}: transcription is behind.")]
    private partial void LogDroppedFrames(long count, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcription failed for session {SessionId} ({ErrorType}).")]
    private partial void LogTranscriptionFailed(Guid sessionId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A transcript listener failed for session {SessionId} ({ErrorType}).")]
    private partial void LogListenerFailed(Guid sessionId, string errorType);

    /// <summary>One failing listener must not stop the others or the transcription itself.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Listener failures are isolated and logged.")]
    private async Task NotifyListenersAsync(IServiceProvider services, Guid sessionId, TranscriptSegment segment, CancellationToken cancellationToken)
    {
        foreach (var listener in services.GetServices<ITranscriptSegmentListener>())
        {
            try
            {
                await listener.OnSegmentAsync(segment, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogListenerFailed(sessionId, ex.GetType().Name);
            }
        }
    }

    private async Task DrainAsync(SessionPipeline pipeline, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in pipeline.Frames.Reader.ReadAllAsync(cancellationToken))
            {
                var dropped = pipeline.TakeNewDrops();
                if (dropped > 0)
                {
                    LogDroppedFrames(dropped, pipeline.SessionId);
                }

                var segment = pipeline.BufferFor(frame.Speaker).Push(frame.Pcm16);
                if (segment is not null)
                {
                    await TranscribeAsync(pipeline.SessionId, frame.Speaker, segment, cancellationToken);
                }
            }

            // The stream ended and the queue is empty: transcribe what each speaker was still saying.
            foreach (var (speaker, segment) in pipeline.FlushAll())
            {
                await TranscribeAsync(pipeline.SessionId, speaker, segment, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }
        finally
        {
            _sink.Release(pipeline);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "One bad segment must not stop the session's transcription; it is reported as an error event.")]
    private async Task TranscribeAsync(Guid sessionId, Speaker speaker, SpeechSegment speech, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<ISpeechToTextProvider>();
            var chunk = new AudioChunk(speaker, speech.Pcm16, AudioFrameParser.SampleRate, speech.StartMs);
            var result = await provider.TranscribeAsync(chunk, null, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                return;
            }

            var segment = TranscriptSegment.Create(sessionId, speaker, speech.StartMs, speech.EndMs, result.Text);
            scope.ServiceProvider.GetRequiredService<ITranscriptRepository>().AddSegment(segment);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);

            _publisher.Publish(
                sessionId,
                SessionEventTypes.TranscriptSegment,
                new { id = segment.Id, speaker = speaker.ToString(), startMs = segment.StartMs, endMs = segment.EndMs, text = segment.Text });

            await NotifyListenersAsync(scope.ServiceProvider, sessionId, segment, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogTranscriptionFailed(sessionId, ex.GetType().Name);

            // Only provider messages written for users reach clients; anything else could expose internals.
            var message = ex is TranscriptionUnavailableException ? ex.Message : GenericFailureMessage;
            _publisher.Publish(sessionId, SessionEventTypes.Error, new { message });
        }
    }
}
