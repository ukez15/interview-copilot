using System.Globalization;

namespace InterviewCopilot.Benchmark;

internal static class Program
{
    private static readonly TimeSpan QuestionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Exit codes: 0 = measured (even when the verdict is FAIL), 1 = backend unreachable or sign-in failed, 2 = bad arguments or questions file.</summary>
    public static async Task<int> Main(string[] args)
    {
        BenchmarkOptions options;
        IReadOnlyList<Question> questions;
        try
        {
            options = BenchmarkOptions.Parse(args, AppContext.BaseDirectory);
            questions = await QuestionBank.LoadAsync(options.QuestionsPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // The per-question 60 s limit is enforced by the runner, so the client itself never times out.
        using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        var runner = new BenchmarkRunner(http, new StopwatchClock(), TimeProvider.System, QuestionTimeout, PasswordGenerator.Generate);
        var hardware = HardwareProbe.Detect();

        Console.WriteLine($"Benchmarking {questions.Count} questions against {options.BaseUrl} (model label: {options.Model})");
        BenchmarkReport report;
        try
        {
            report = await runner.RunAsync(questions, options.Model, hardware, new Progress<QuestionResult>(PrintProgress), cts.Token);
        }
        catch (BenchmarkSetupException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled.");
            return 1;
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await File.WriteAllTextAsync(options.OutputPath, ReportRenderer.Render(report), CancellationToken.None);
        PrintSummary(report, options.OutputPath);
        return 0;
    }

    private static void PrintProgress(QuestionResult r) =>
        Console.WriteLine(r.Succeeded
            ? string.Create(CultureInfo.InvariantCulture, $"  {r.Number,2} {r.Type,-20} TTFT {r.TtftMs:F0} ms, total {r.TotalMs:F0} ms")
            : string.Create(CultureInfo.InvariantCulture, $"  {r.Number,2} {r.Type,-20} FAILED: {r.Error}"));

    private static void PrintSummary(BenchmarkReport report, string outputPath)
    {
        var s = LatencyStats.Summarize(report.Results.ToArray());
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{s.Succeeded}/{s.Total} succeeded. TTFT P50 {Format(s.TtftP50)} ms, P95 {Format(s.TtftP95)} ms. Total P50 {Format(s.TotalP50)} ms, P95 {Format(s.TotalP95)} ms."));
        Console.WriteLine($"NFR-05 (P95 TTFT <= {LatencyStats.NfrTtftLimitMs:F0} ms): {(s.Passes ? "PASS" : "FAIL")}");
        Console.WriteLine($"Report written to {outputPath}");
    }

    private static string Format(double? value) =>
        value is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : "-";
}
