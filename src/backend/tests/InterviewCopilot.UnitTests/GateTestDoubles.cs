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

    /// <summary>How far transcription has caught up; unset means it keeps up with <see cref="Position"/>.</summary>
    public long? TranscribedThrough { get; set; }

    public long PositionMs(Guid sessionId, Speaker speaker) => Position;

    public long TranscribedThroughMs(Guid sessionId, Speaker speaker) => TranscribedThrough ?? Position;
}

/// <summary>Fires every timer at once and counts them, so a test can run a long wait instantly.</summary>
internal sealed class InstantTimers : TimeProvider
{
    private int _created;

    public int Created => Volatile.Read(ref _created);

    public override DateTimeOffset GetUtcNow() => FixedClock.Default;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _created);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
            // Nothing to release: the callback already ran.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
