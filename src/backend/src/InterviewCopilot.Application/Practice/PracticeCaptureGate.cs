using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.Application.Practice;

/// <summary>
/// In a mock interview, transcribes the candidate only while a question is open (#78): between questions their
/// speech isn't wanted, and leaving Whisper idle gives the model the CPU to write the next question or score.
/// Every other session mode, and the interviewer's audio, is always transcribed.
/// </summary>
public sealed class PracticeCaptureGate : IAudioCaptureGate
{
    private readonly IServiceScopeFactory _scopes;
    private readonly PracticeAnswerCollector _collector;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<bool?>>> _mockSessions = new();

    /// <summary>Creates the gate.</summary>
    /// <param name="scopes">Creates a scope for the scoped session repository.</param>
    /// <param name="collector">Knows which sessions have a question open.</param>
    public PracticeCaptureGate(IServiceScopeFactory scopes, PracticeAnswerCollector collector)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(collector);
        _scopes = scopes;
        _collector = collector;
    }

    /// <inheritdoc />
    public async ValueTask<bool> AcceptsAsync(Guid sessionId, Speaker speaker, CancellationToken cancellationToken)
    {
        if (speaker != Speaker.Candidate || _collector.IsOpen(sessionId))
        {
            return true;
        }

        var lookup = _mockSessions.GetOrAdd(sessionId, id => new Lazy<Task<bool?>>(() => IsMockInterviewAsync(id)));
        var isMock = await lookup.Value.WaitAsync(cancellationToken);
        if (isMock is null)
        {
            // The session couldn't be read: transcribe this frame and look again on the next one.
            _mockSessions.TryRemove(new KeyValuePair<Guid, Lazy<Task<bool?>>>(sessionId, lookup));
            return true;
        }

        return !isMock.Value;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed lookup must never break audio ingest; the frame is transcribed and the lookup retried.")]
    private async Task<bool?> IsMockInterviewAsync(Guid sessionId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .GetAsync(sessionId, CancellationToken.None);
            return session is null ? null : session.Mode == SessionMode.MockInterview;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
