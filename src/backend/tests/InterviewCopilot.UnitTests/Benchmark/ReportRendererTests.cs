using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class ReportRendererTests
{
    private static readonly HardwareInfo Hardware = new("Test CPU @ 3.00GHz", 8, 16L * 1024 * 1024 * 1024, "Test OS 1.0");

    [Fact]
    public void Render_ShouldIncludeHardwareModelDateAndVerdict()
    {
        var report = new BenchmarkReport(
            new DateTimeOffset(2026, 10, 16, 12, 30, 0, TimeSpan.Zero),
            Hardware,
            "llama3.2:3b",
            [new QuestionResult(1, "Behavioral", 1200.4, 6000.6, null), new QuestionResult(2, "Coding", 2000, 7000, null)]);

        var markdown = ReportRenderer.Render(report);

        markdown.Should().Contain("2026-10-16 12:30:00");
        markdown.Should().Contain("Test CPU @ 3.00GHz");
        markdown.Should().Contain("Logical cores: 8");
        markdown.Should().Contain("16.0 GiB");
        markdown.Should().Contain("Test OS 1.0");
        markdown.Should().Contain("llama3.2:3b");
        markdown.Should().Contain("NFR-05");
        markdown.Should().Contain("**PASS**");
    }

    [Fact]
    public void Render_ShouldListEveryQuestionWithRoundedTimings()
    {
        var report = new BenchmarkReport(
            DateTimeOffset.UnixEpoch,
            Hardware,
            "m",
            [new QuestionResult(1, "Behavioral", 1200.4, 6000.6, null), new QuestionResult(2, "Coding", null, null, "timed out after 60 s")]);

        var markdown = ReportRenderer.Render(report);

        markdown.Should().Contain("| 1 | Behavioral | 1200 | 6001 | ok |");
        markdown.Should().Contain("| 2 | Coding | - | - | failed: timed out after 60 s |");
        markdown.Should().Contain("1 of 2");
    }

    [Fact]
    public void Render_ShouldShowFailVerdict_WhenP95AboveFiveSeconds()
    {
        var report = new BenchmarkReport(
            DateTimeOffset.UnixEpoch,
            Hardware,
            "m",
            [new QuestionResult(1, "Coding", 5001, 9000, null)]);

        ReportRenderer.Render(report).Should().Contain("**FAIL**");
    }

    [Fact]
    public void Render_ShouldNotThrowAndShowFail_WhenAllRunsFailed()
    {
        var report = new BenchmarkReport(
            DateTimeOffset.UnixEpoch,
            Hardware,
            "m",
            [new QuestionResult(1, "Coding", null, null, "boom")]);

        var markdown = ReportRenderer.Render(report);

        markdown.Should().Contain("**FAIL**");
        markdown.Should().Contain("no successful runs");
    }

    [Fact]
    public void Render_ShouldEscapePipesAndNewlinesInErrors()
    {
        var report = new BenchmarkReport(
            DateTimeOffset.UnixEpoch,
            Hardware,
            "m",
            [new QuestionResult(1, "Coding", null, null, "a|b\nc")]);

        var markdown = ReportRenderer.Render(report);

        markdown.Should().Contain("failed: a\\|b c");
    }

    [Fact]
    public void Render_ShouldUseInvariantCultureNumbers()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var report = new BenchmarkReport(
                DateTimeOffset.UnixEpoch,
                new HardwareInfo("cpu", 4, 8L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "os"),
                "m",
                [new QuestionResult(1, "Coding", 1500, 2500, null)]);

            ReportRenderer.Render(report).Should().Contain("8.5 GiB");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
