using InterviewCopilot.Application.Audio;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests;

/// <summary>A gate the test opens and closes by hand.</summary>
internal sealed class SwitchGate : IAudioCaptureGate
{
    public bool Accept { get; set; } = true;

    public ValueTask<bool> AcceptsAsync(Guid sessionId, Speaker speaker, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Accept);
}

/// <summary>A stream clock the test sets by hand.</summary>
internal sealed class FixedStreamClock : IAudioStreamClock
{
    public long Position { get; set; }

    public long PositionMs(Guid sessionId, Speaker speaker) => Position;
}
