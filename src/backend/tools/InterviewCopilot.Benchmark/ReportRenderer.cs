using System.Globalization;
using System.Text;

namespace InterviewCopilot.Benchmark;

internal static class ReportRenderer
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;

    public static string Render(BenchmarkReport report)
    {
        var summary = LatencyStats.Summarize(report.Results.ToArray());
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.Append("# Copilot latency benchmark (NFR-05)\n\n");
        sb.Append("Time to first answer token (TTFT) and total time for each question, measured through the real HTTP API.\n\n");
        sb.Append(inv, $"- Date (UTC): {report.DateUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append(inv, $"- Model: {report.Model}\n");
        sb.Append(inv, $"- CPU: {report.Hardware.Cpu}\n");
        sb.Append(inv, $"- Logical cores: {report.Hardware.LogicalCores}\n");
        sb.Append(inv, $"- RAM: {(report.Hardware.TotalRamBytes / BytesPerGibibyte).ToString("F1", inv)} GiB\n");
        sb.Append(inv, $"- OS: {report.Hardware.Os}\n");
        sb.Append(inv, $"- Questions: {summary.Succeeded} of {summary.Total} succeeded\n\n");

        sb.Append("## Results\n\n");
        sb.Append("| # | Type | TTFT (ms) | Total (ms) | Status |\n");
        sb.Append("|---|------|-----------|------------|--------|\n");
        foreach (var r in report.Results)
        {
            var status = r.Succeeded ? "ok" : $"failed: {Escape(r.Error!)}";
            sb.Append(inv, $"| {r.Number} | {r.Type} | {Ms(r.Succeeded ? r.TtftMs : null)} | {Ms(r.Succeeded ? r.TotalMs : null)} | {status} |\n");
        }

        sb.Append("\n## Summary (successful runs, nearest-rank percentiles)\n\n");
        sb.Append("| Metric | P50 (ms) | P95 (ms) |\n");
        sb.Append("|--------|----------|----------|\n");
        sb.Append(inv, $"| Time to first token | {Ms(summary.TtftP50)} | {Ms(summary.TtftP95)} |\n");
        sb.Append(inv, $"| Total time | {Ms(summary.TotalP50)} | {Ms(summary.TotalP95)} |\n\n");

        var verdict = summary.Passes ? "PASS" : "FAIL";
        var detail = summary.TtftP95 is { } p95
            ? $"P95 TTFT {Ms(p95)} ms against the {Ms(LatencyStats.NfrTtftLimitMs)} ms limit"
            : "no successful runs";
        sb.Append(inv, $"NFR-05 verdict: **{verdict}** ({detail}).\n");
        return sb.ToString();
    }

    private static string Ms(double? value) =>
        value is { } v ? Math.Round(v, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) : "-";

    private static string Escape(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace("|", "\\|", StringComparison.Ordinal);
}
