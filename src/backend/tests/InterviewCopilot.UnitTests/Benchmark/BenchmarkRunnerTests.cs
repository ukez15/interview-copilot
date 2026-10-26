using System.Net;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class BenchmarkRunnerTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly HardwareInfo Hardware = new("cpu", 4, 1L << 33, "os");
    private static readonly FixedTimeProvider Now = new(new DateTimeOffset(2026, 10, 16, 9, 8, 7, TimeSpan.Zero));

    private static readonly string[] OkStream =
    [
        "event: meta\ndata: {}\n\nevent: delta\ndata: A\n\nevent: done\ndata: \n\n",
    ];

    [Fact]
    public async Task RunAsync_ShouldRegisterSignInCreateStartAskWarmUpAndQuestionsThenEnd()
    {
        var handler = ScriptedBackend(_ => DelegateHandler.Sse(OkStream));
        var runner = NewRunner(handler);
        Question[] questions = [new("Coding", "Q1?"), new("Behavioral", "Q2?")];

        var report = await runner.RunAsync(questions, "llama3.2:3b", Hardware, progress: null, CancellationToken.None);

        var paths = handler.Requests.Select(r => $"{r.Method} {r.Path}").ToArray();
        paths.Should().Equal(
            "POST /api/auth/register",
            "POST /api/auth/sign-in",
            "POST /api/sessions",
            $"POST /api/sessions/{SessionId}/start",
            $"POST /api/sessions/{SessionId}/answers", // warm-up
            $"POST /api/sessions/{SessionId}/answers",
            $"POST /api/sessions/{SessionId}/answers",
            $"POST /api/sessions/{SessionId}/end");
        report.Results.Should().HaveCount(2); // warm-up is not counted
        report.Results.Select(r => r.Number).Should().Equal(1, 2);
        report.Results.Should().OnlyContain(r => r.Succeeded);
        report.Model.Should().Be("llama3.2:3b");
        report.DateUtc.Should().Be(Now.GetUtcNow());
    }

    [Fact]
    public async Task RunAsync_ShouldUseTimestampedUsernameRandomPasswordAndLiveBalancedSession()
    {
        var handler = ScriptedBackend(_ => DelegateHandler.Sse(OkStream));
        var runner = NewRunner(handler);

        await runner.RunAsync([new("Coding", "Q?")], "m", Hardware, null, CancellationToken.None);

        using var register = JsonDocument.Parse(handler.Requests[0].Body);
        register.RootElement.GetProperty("username").GetString().Should().Be("benchmark-20261016090807");
        var registered = register.RootElement.GetProperty("password").GetString();
        registered.Should().NotBeNullOrEmpty();
        using var signIn = JsonDocument.Parse(handler.Requests[1].Body);
        signIn.RootElement.GetProperty("password").GetString().Should().Be(registered);

        using var session = JsonDocument.Parse(handler.Requests[2].Body);
        session.RootElement.GetProperty("mode").GetString().Should().Be("LiveInterview");
        session.RootElement.GetProperty("answerLength").GetString().Should().Be("Balanced");
        session.RootElement.GetProperty("framework").GetString().Should().Be("None");
    }

    [Fact]
    public async Task RunAsync_ShouldRecordFailedQuestionAndContinue()
    {
        var calls = 0;
        var handler = ScriptedBackend(_ => ++calls == 2 // call 1 is the warm-up, call 2 is question 1
            ? DelegateHandler.Sse(["event: meta\ndata: {}\n\nevent: error\ndata: down\n\n"])
            : DelegateHandler.Sse(OkStream));
        var runner = NewRunner(handler);

        var report = await runner.RunAsync([new("Coding", "Q1?"), new("Coding", "Q2?")], "m", Hardware, null, CancellationToken.None);

        report.Results[0].Succeeded.Should().BeFalse();
        report.Results[1].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ShouldReportEachResultToProgress()
    {
        var handler = ScriptedBackend(_ => DelegateHandler.Sse(OkStream));
        var runner = NewRunner(handler);
        var seen = new List<QuestionResult>();

        await runner.RunAsync([new("Coding", "Q1?"), new("Coding", "Q2?")], "m", Hardware, new SyncProgress(seen.Add), CancellationToken.None);

        seen.Select(r => r.Number).Should().Equal(1, 2);
    }

    [Fact]
    public async Task RunAsync_ShouldFailWithStartBackendHint_WhenBackendUnreachable()
    {
        var handler = new DelegateHandler((_, _) => throw new HttpRequestException("refused"));
        var runner = NewRunner(handler);

        var act = () => runner.RunAsync([new("Coding", "Q?")], "m", Hardware, null, CancellationToken.None);

        await act.Should().ThrowAsync<BenchmarkSetupException>().WithMessage("*Start the backend first: pnpm dev:backend*");
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenSignInIsRejected()
    {
        var handler = new DelegateHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/register" => DelegateHandler.Json(HttpStatusCode.Created, "{}"),
            _ => DelegateHandler.Json(HttpStatusCode.Unauthorized, "{}"),
        }));
        var runner = NewRunner(handler);

        var act = () => runner.RunAsync([new("Coding", "Q?")], "m", Hardware, null, CancellationToken.None);

        await act.Should().ThrowAsync<BenchmarkSetupException>().WithMessage("*sign in*");
    }

    [Fact]
    public async Task RunAsync_ShouldStillEndSession_WhenCancelledMidRun()
    {
        using var cts = new CancellationTokenSource();
        var handler = ScriptedBackend(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var runner = NewRunner(handler);

        var act = () => runner.RunAsync([new("Coding", "Q?")], "m", Hardware, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests[^1].Path.Should().EndWith("/end");
    }

    [Fact]
    public void PasswordGenerator_ShouldProduceLongDistinctValues()
    {
        var first = PasswordGenerator.Generate();
        var second = PasswordGenerator.Generate();

        first.Length.Should().BeGreaterThanOrEqualTo(24);
        first.Should().NotBe(second);
    }

    private static BenchmarkRunner NewRunner(DelegateHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") },
            new FakeClock(),
            Now,
            TimeSpan.FromSeconds(5),
            () => Convert.ToHexString(Guid.NewGuid().ToByteArray()));

    private static DelegateHandler ScriptedBackend(Func<HttpRequestMessage, HttpResponseMessage> answers) =>
        new((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                "/api/auth/register" => DelegateHandler.Json(HttpStatusCode.Created, "{}"),
                "/api/auth/sign-in" => DelegateHandler.Json(HttpStatusCode.OK, """{"tokenType":"Bearer","accessToken":"t","expiresIn":3600,"refreshToken":"r"}"""),
                "/api/sessions" => DelegateHandler.Json(HttpStatusCode.Created, $$"""{"id":"{{SessionId}}"}"""),
                _ when path.EndsWith("/answers", StringComparison.Ordinal) => answers(request),
                _ => DelegateHandler.Json(HttpStatusCode.OK, "{}"),
            });
        });

    private sealed class SyncProgress(Action<QuestionResult> report) : IProgress<QuestionResult>
    {
        public void Report(QuestionResult value) => report(value);
    }
}
