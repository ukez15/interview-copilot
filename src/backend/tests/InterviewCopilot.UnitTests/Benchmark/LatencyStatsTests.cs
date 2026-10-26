using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class LatencyStatsTests
{
    [Theory]
    [InlineData(50, 5)]
    [InlineData(51, 6)]
    [InlineData(95, 10)]
    [InlineData(100, 10)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(1, 1)]
    public void Percentile_ShouldUseNearestRank_WhenTenUnsortedSamples(double percentile, double expected)
    {
        double[] samples = [10, 3, 8, 7, 6, 5, 4, 9, 2, 1];

        LatencyStats.Percentile(samples, percentile).Should().Be(expected);
    }

    [Fact]
    public void Percentile_ShouldReturnTheSample_WhenOnlyOneSample()
    {
        LatencyStats.Percentile([42], 50).Should().Be(42);
        LatencyStats.Percentile([42], 95).Should().Be(42);
    }

    [Fact]
    public void Percentile_ShouldReturnNull_WhenNoSamples()
    {
        LatencyStats.Percentile([], 95).Should().BeNull();
    }

    [Fact]
    public void Percentile_ShouldPickNinetyFifthRank_WhenTwentySamples()
    {
        var samples = Enumerable.Range(1, 20).Select(i => (double)i * 100).Reverse().ToArray();

        LatencyStats.Percentile(samples, 95).Should().Be(1900); // ceil(0.95 * 20) = 19th
        LatencyStats.Percentile(samples, 50).Should().Be(1000); // ceil(0.50 * 20) = 10th
    }

    [Fact]
    public void Summarize_ShouldIgnoreFailedRuns_WhenComputingPercentiles()
    {
        QuestionResult[] results =
        [
            new(1, "Coding", 1000, 4000, null),
            new(2, "Coding", 2000, 5000, null),
            new(3, "Coding", null, null, "timed out"),
        ];

        var summary = LatencyStats.Summarize(results);

        summary.Total.Should().Be(3);
        summary.Succeeded.Should().Be(2);
        summary.Failed.Should().Be(1);
        summary.TtftP50.Should().Be(1000);
        summary.TtftP95.Should().Be(2000);
        summary.TotalP95.Should().Be(5000);
        summary.Passes.Should().BeTrue();
    }

    [Fact]
    public void Summarize_ShouldFail_WhenEveryRunFailed()
    {
        QuestionResult[] results = [new(1, "Coding", null, null, "boom"), new(2, "Coding", null, null, "boom")];

        var summary = LatencyStats.Summarize(results);

        summary.Succeeded.Should().Be(0);
        summary.TtftP50.Should().BeNull();
        summary.TtftP95.Should().BeNull();
        summary.Passes.Should().BeFalse();
    }

    [Theory]
    [InlineData(5000, true)]
    [InlineData(5000.5, false)]
    [InlineData(100, true)]
    public void Passes_ShouldCompareP95TtftWithFiveSeconds(double ttft, bool expected)
    {
        var summary = LatencyStats.Summarize([new QuestionResult(1, "Coding", ttft, ttft + 1, null)]);

        summary.Passes.Should().Be(expected);
    }
}
