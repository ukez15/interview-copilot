using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Practice;

/// <summary>
/// Notices when the candidate has finished answering (#78): once they said something, everything they said is
/// transcribed and <see cref="SilenceMs"/> pass with no voice, it announces <see cref="SessionEventTypes.PracticeSilence"/>
/// once, so the overlay can score. Silence is measured on the audio, so slow transcription can't cut an answer short.
/// </summary>
public sealed class PracticeSilenceWatcher(PracticeAnswerCollector collector, ISessionEventPublisher events) : IAudioProgressListener
{
    /// <summary>How long the candidate must be quiet before the answer counts as finished (owner's choice).</summary>
    public const int SilenceMs = 6_000;

    /// <inheritdoc />
    public ValueTask OnFrameHandledAsync(AudioProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Speaker == Speaker.Candidate
            && !progress.SpeechPending
            && progress.PositionMs - progress.LastVoiceEndMs >= SilenceMs
            && collector.TryClaimSilence(progress.SessionId))
        {
            events.Publish(progress.SessionId, SessionEventTypes.PracticeSilence, new { });
        }

        return ValueTask.CompletedTask;
    }
}
