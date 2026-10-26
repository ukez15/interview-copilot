namespace InterviewCopilot.Benchmark;

internal sealed record LatencySummary(
    int Total,
    int Succeeded,
    double? TtftP50,
    double? TtftP95,
    double? TotalP50,
    double? TotalP95)
{
    public int Failed => Total - Succeeded;

    /// <summary>NFR-05: the P95 time to first token is at most 5 s. No successful run means no pass.</summary>
    public bool Passes => TtftP95 is { } p95 && p95 <= LatencyStats.NfrTtftLimitMs;
}

internal static class LatencyStats
{
    /// <summary>NFR-05 upper bound for time to first answer token, in milliseconds.</summary>
    public const double NfrTtftLimitMs = 5000;

    /// <summary>Nearest-rank percentile: the value at rank ceil(p/100 * n) of the sorted samples. Null when empty.</summary>
    public static double? Percentile(IReadOnlyCollection<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var sorted = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    public static LatencySummary Summarize(IReadOnlyCollection<QuestionResult> results)
    {
        var ok = results.Where(r => r.Succeeded && r.TtftMs is not null && r.TotalMs is not null).ToArray();
        var ttft = ok.Select(r => r.TtftMs!.Value).ToArray();
        var total = ok.Select(r => r.TotalMs!.Value).ToArray();
        return new LatencySummary(
            results.Count,
            ok.Length,
            Percentile(ttft, 50),
            Percentile(ttft, 95),
            Percentile(total, 50),
            Percentile(total, 95));
    }
}
