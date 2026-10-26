using System.Net;
using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class QuestionRunnerTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Question Sample = new("Coding", "Reverse a linked list.");

    [Fact]
    public async Task RunAsync_ShouldTimeFirstDeltaAndDone_WhenStreamSucceeds()
    {
        var clock = new FakeClock();
        var handler = new DelegateHandler((_, _) => Task.FromResult(DelegateHandler.Sse(
            [
                "event: meta\ndata: {}\n\n",
                "event: delta\ndata: Hel\n\n",
                "event: delta\ndata: lo\n\n",
                "event: done\ndata: \n\n",
            ],
            beforeChunk: i => clock.Advance(i switch { 0 => TimeSpan.FromMilliseconds(100), 1 => TimeSpan.FromMilliseconds(900), 2 => TimeSpan.FromMilliseconds(500), _ => TimeSpan.FromMilliseconds(250) }))));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, clock, TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 3, Sample, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Number.Should().Be(3);
        result.Type.Should().Be("Coding");
        result.TtftMs.Should().Be(1000); // 100 (meta) + 900 (first delta)
        result.TotalMs.Should().Be(1750);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be($"/api/sessions/{SessionId}/answers");
        handler.Requests[0].Body.Should().Contain("Reverse a linked list.");
    }

    [Fact]
    public async Task RunAsync_ShouldDetectFirstDelta_WhenEventSplitAcrossChunks()
    {
        var clock = new FakeClock();
        var handler = new DelegateHandler((_, _) => Task.FromResult(DelegateHandler.Sse(
            ["event: me", "ta\ndata: {}\n\nevent: del", "ta\ndata: A\n\nevent: done\n", "data: \n\n"],
            beforeChunk: _ => clock.Advance(TimeSpan.FromMilliseconds(10)))));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, clock, TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.TtftMs.Should().Be(30); // the delta event only completes in the third chunk
        result.TotalMs.Should().Be(40);
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenStreamSendsErrorEvent()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(DelegateHandler.Sse(
            ["event: meta\ndata: {}\n\nevent: error\ndata: Ollama is not running\n\n"])));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.TtftMs.Should().BeNull();
        result.Error.Should().Contain("Ollama is not running");
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenStreamEndsWithoutDone()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(DelegateHandler.Sse(
            ["event: meta\ndata: {}\n\nevent: delta\ndata: A\n\n"])));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("done");
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenDoneArrivesWithoutAnyToken()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(DelegateHandler.Sse(
            ["event: meta\ndata: {}\n\nevent: done\ndata: \n\n"])));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("no tokens");
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenServerReturnsErrorStatus()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(DelegateHandler.Json(HttpStatusCode.NotFound, "{}")));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("404");
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenConnectionDrops()
    {
        var handler = new DelegateHandler((_, _) => throw new HttpRequestException("connection reset"));
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("connection reset");
    }

    [Fact]
    public async Task RunAsync_ShouldRecordTimeout_WhenNoResponseWithinLimit()
    {
        var handler = new DelegateHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromMilliseconds(50));

        var result = await runner.RunAsync(SessionId, 1, Sample, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateCancellation_WhenCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        var handler = new DelegateHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var runner = new QuestionRunner(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, new FakeClock(), TimeSpan.FromSeconds(30));

        var act = () => runner.RunAsync(SessionId, 1, Sample, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
