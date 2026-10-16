using System.Net;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Llm;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class OllamaModelCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OllamaModelCatalog Catalog(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") });

    [Fact]
    public async Task Should_ParseInstalledModels_FromTags()
    {
        const string body = """
            {"models":[
              {"name":"llama3.2:3b","modified_at":"2026-09-01T10:00:00Z","size":2019393189,"digest":"abc"},
              {"name":"qwen2.5:7b","modified_at":"2026-09-02T11:30:00Z","size":4683087332}
            ]}
            """;
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        var models = await Catalog(handler).ListInstalledAsync(Ct);

        models.Should().HaveCount(2);
        models[0].Should().Be(new OllamaModelInfo("llama3.2:3b", 2_019_393_189, new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)));
        models[1].Name.Should().Be("qwen2.5:7b");
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/tags");
        handler.LastRequest.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task Should_ReturnEmpty_When_NoModelsInstalled()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var models = await Catalog(handler).ListInstalledAsync(Ct);

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_ExplainOllamaIsDown_When_ConnectionFails()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("refused"));

        var act = () => Catalog(handler).ListInstalledAsync(Ct);

        await act.Should().ThrowAsync<LlmUnavailableException>()
            .WithMessage("Can't reach Ollama. Make sure it's installed and running.");
    }

    [Fact]
    public async Task Should_ExplainOllamaIsDown_When_RequestTimesOut()
    {
        var handler = new FakeHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));

        var act = () => Catalog(handler).ListInstalledAsync(Ct);

        await act.Should().ThrowAsync<LlmUnavailableException>().WithMessage("Can't reach Ollama*");
    }

    [Fact]
    public async Task Should_ExplainOllamaIsDown_When_OllamaReturnsError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => Catalog(handler).ListInstalledAsync(Ct);

        await act.Should().ThrowAsync<LlmUnavailableException>().WithMessage("Can't reach Ollama*");
    }

    [Fact]
    public async Task Should_Propagate_When_CallerCancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHandler(_ => throw new OperationCanceledException(cts.Token));

        var act = () => Catalog(handler).ListInstalledAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
