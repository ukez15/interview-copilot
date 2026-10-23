using System.Net;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Llm;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class CloudModelCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewKey() => "sk-" + Guid.NewGuid().ToString("N");

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://models.test/") };

    private static CloudFakeHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status, body, "application/json");

    [Fact]
    public async Task Should_ListAnthropicModels_WithKeyHeaders()
    {
        var key = NewKey();
        var handler = Json("""{"data":[{"id":"claude-b","display_name":"Claude B"},{"id":"claude-a"},{"display_name":"no id"}],"has_more":false}""");
        var catalog = new AnthropicModelCatalog(Client(handler));

        var models = await catalog.ListAsync(key, Ct);

        catalog.Provider.Should().Be(ProviderNames.Anthropic);
        models.Should().Equal(new CloudModelInfo("claude-a", "claude-a"), new CloudModelInfo("claude-b", "Claude B"));
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/v1/models");
        handler.LastRequest.Headers.GetValues("x-api-key").Should().Equal(key);
        handler.LastRequest.Headers.GetValues("anthropic-version").Should().Equal("2023-06-01");
    }

    [Fact]
    public async Task Should_ListOpenAiModels_UsingIdAsDisplayName()
    {
        var key = NewKey();
        var handler = Json("""{"object":"list","data":[{"id":"gpt-b"},{"id":"gpt-a"},{"object":"model"}]}""");
        var catalog = new OpenAiModelCatalog(Client(handler));

        var models = await catalog.ListAsync(key, Ct);

        catalog.Provider.Should().Be(ProviderNames.OpenAi);
        models.Should().Equal(new CloudModelInfo("gpt-a", "gpt-a"), new CloudModelInfo("gpt-b", "gpt-b"));
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1/models");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be(key);
    }

    [Fact]
    public async Task Should_ListGeminiModels_ThatCanGenerateContent()
    {
        var key = NewKey();
        var handler = Json("""
            {"models":[
              {"name":"models/gemini-2.0-flash","displayName":"Gemini 2.0 Flash","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/embedding-001","displayName":"Embedding","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-x","supportedGenerationMethods":["generateContent"]},
              {"name":"bare-name","displayName":"Bare","supportedGenerationMethods":["generateContent"]},
              {"displayName":"nameless","supportedGenerationMethods":["generateContent"]},
              {"name":"models/no-methods","displayName":"None"}
            ]}
            """);
        var catalog = new GoogleModelCatalog(Client(handler));

        var models = await catalog.ListAsync(key, Ct);

        catalog.Provider.Should().Be(ProviderNames.Google);
        models.Should().Equal(
            new CloudModelInfo("bare-name", "Bare"),
            new CloudModelInfo("gemini-2.0-flash", "Gemini 2.0 Flash"),
            new CloudModelInfo("gemini-x", "gemini-x"));
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1beta/models");
        handler.LastRequest.Headers.GetValues("x-goog-api-key").Should().Equal(key);
        handler.LastRequest.RequestUri.ToString().Should().NotContain(key);
    }

    public static TheoryData<string> Catalogs => new() { "anthropic", "openai", "google" };

    private static ICloudModelCatalog Build(string name, HttpMessageHandler handler) => name switch
    {
        "anthropic" => new AnthropicModelCatalog(Client(handler)),
        "openai" => new OpenAiModelCatalog(Client(handler)),
        _ => new GoogleModelCatalog(Client(handler)),
    };

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Should_ReturnEmpty_When_ReplyHasNoModels(string name)
    {
        (await Build(name, Json("{}")).ListAsync(NewKey(), Ct)).Should().BeEmpty();
        (await Build(name, Json("[]")).ListAsync(NewKey(), Ct)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Should_ThrowKeyRejected_When_ProviderReturns401Or403(string name)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var key = NewKey();

            var act = () => Build(name, Json($"{{\"error\":\"{key}\"}}", status)).ListAsync(key, Ct);

            var thrown = await act.Should().ThrowAsync<LlmKeyRejectedException>();
            thrown.Which.Message.Should().EndWith("rejected the API key.").And.NotContain(key);
        }
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Should_ThrowUnavailable_When_RateLimitedOrBroken(string name)
    {
        var limited = () => Build(name, Json("{}", HttpStatusCode.TooManyRequests)).ListAsync(NewKey(), Ct);
        var broken = () => Build(name, Json("{}", HttpStatusCode.BadGateway)).ListAsync(NewKey(), Ct);
        var offline = () => Build(name, new CloudFakeHandler(_ => throw new HttpRequestException("refused"))).ListAsync(NewKey(), Ct);
        var timeout = () => Build(name, new CloudFakeHandler(_ => throw new TaskCanceledException("t", new TimeoutException()))).ListAsync(NewKey(), Ct);
        var garbage = () => Build(name, Json("not json")).ListAsync(NewKey(), Ct);

        (await limited.Should().ThrowAsync<LlmUnavailableException>()).Which.Message.Should().Contain("rate limiting");
        (await broken.Should().ThrowAsync<LlmUnavailableException>()).Which.Message.Should().Contain("(502)");
        (await offline.Should().ThrowAsync<LlmUnavailableException>()).Which.Message.Should().StartWith("Can't reach");
        (await timeout.Should().ThrowAsync<LlmUnavailableException>()).Which.Message.Should().StartWith("Can't reach");
        (await garbage.Should().ThrowAsync<LlmUnavailableException>()).Which.Message.Should().Contain("couldn't be read");
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Should_Propagate_When_CallerCancels(string name)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new CloudFakeHandler(_ => throw new OperationCanceledException(cts.Token));

        var act = () => Build(name, handler).ListAsync(NewKey(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Should_RejectBlankKey(string name)
    {
        var act = () => Build(name, Json("{}")).ListAsync(" ", Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
