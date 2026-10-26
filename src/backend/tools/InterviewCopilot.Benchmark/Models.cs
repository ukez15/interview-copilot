namespace InterviewCopilot.Benchmark;

/// <summary>One benchmark question and its question-type name (matches the backend's QuestionType enum).</summary>
internal sealed record Question(string Type, string Text);

/// <summary>Outcome of one question. A failed run keeps its error and is excluded from the statistics.</summary>
internal sealed record QuestionResult(int Number, string Type, double? TtftMs, double? TotalMs, string? Error)
{
    public bool Succeeded => Error is null;
}

internal sealed record HardwareInfo(string Cpu, int LogicalCores, long TotalRamBytes, string Os);

internal sealed record BenchmarkReport(
    DateTimeOffset DateUtc,
    HardwareInfo Hardware,
    string Model,
    IReadOnlyList<QuestionResult> Results);

/// <summary>Raised when the benchmark cannot run at all (backend down, sign-in refused). Exit code 1.</summary>
public sealed class BenchmarkSetupException : Exception
{
    public BenchmarkSetupException()
    {
    }

    public BenchmarkSetupException(string message)
        : base(message)
    {
    }

    public BenchmarkSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Monotonic time source, replaceable in tests.</summary>
internal interface IClock
{
    long GetTimestamp();

    TimeSpan GetElapsed(long startTimestamp);
}
