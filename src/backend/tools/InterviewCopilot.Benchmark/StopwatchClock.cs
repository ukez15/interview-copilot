using System.Diagnostics;

namespace InterviewCopilot.Benchmark;

internal sealed class StopwatchClock : IClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
