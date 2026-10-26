using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class BenchmarkOptionsTests
{
    [Fact]
    public void Parse_ShouldUseDefaults_WhenNoArguments()
    {
        var options = BenchmarkOptions.Parse([], "/app");

        options.BaseUrl.Should().Be("http://127.0.0.1:5480");
        options.QuestionsPath.Should().Be(Path.Combine("/app", "questions.json"));
        options.OutputPath.Should().Be(Path.Combine("docs", "benchmarks", "latency.md"));
        options.Model.Should().Be("llama3.2:3b");
    }

    [Fact]
    public void Parse_ShouldReadSeparatedAndEqualsForms()
    {
        var options = BenchmarkOptions.Parse(
            ["--base-url", "http://localhost:9999", "--questions=q.json", "--output", "out.md", "--model=phi3"],
            "/app");

        options.BaseUrl.Should().Be("http://localhost:9999");
        options.QuestionsPath.Should().Be("q.json");
        options.OutputPath.Should().Be("out.md");
        options.Model.Should().Be("phi3");
    }

    [Theory]
    [InlineData("--unknown", "x")]
    [InlineData("--model")]
    [InlineData("positional")]
    public void Parse_ShouldRejectBadArguments(params string[] args)
    {
        var act = () => BenchmarkOptions.Parse(args, "/app");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldRejectInvalidBaseUrl()
    {
        var act = () => BenchmarkOptions.Parse(["--base-url", "not a url"], "/app");

        act.Should().Throw<ArgumentException>().WithMessage("*base-url*");
    }
}
