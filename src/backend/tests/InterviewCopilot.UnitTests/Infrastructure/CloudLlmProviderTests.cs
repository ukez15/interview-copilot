using System.Net;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Llm;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class CloudLlmProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewKey() => "sk-" + Guid.NewGuid().ToString("N");

    private static LlmRequest Request(string? key, string model = "the-model") =>
        new(
            model,
            [
                new LlmMessage(LlmRole.System, "rules"),
                new LlmMessage(LlmRole.System, "more rules"),
                new LlmMessage(LlmRole.User, "q1"),
                new LlmMessage(LlmRole.Assistant, "a1"),
                new LlmMessage(LlmRole.User, "q2"),
            ],
            0.3,
            key);

    private static HttpClient Client(HttpMessageHandler handler, string baseUrl) =>
        new(handler) { BaseAddress = new Uri(baseUrl) };

    private static Task<List<string>> CollectAsync(ILlmProvider provider, LlmRequest request) =>
        provider.StreamAsync(request, Ct).ToListAsync(Ct).AsTask();

    private static AnthropicLlmProvider Anthropic(HttpMessageHandler handler) => new(Client(handler, "https://anthropic.test/"));

    private static OpenAiLlmProvider OpenAi(HttpMessageHandler handler) => new(Client(handler, "https://openai.test/"));

    private static GoogleLlmProvider Google(HttpMessageHandler handler) => new(Client(handler, "https://google.test/"));

    // ---- Anthropic ----

    private const string AnthropicSse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"m1"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

        event: ping
        data: {"type":"ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

        event: message_stop
        data: {"type":"message_stop"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ignored"}}
        """;

    [Fact]
    public async Task Should_StreamAnthropicText_AndSendTheDocumentedRequest()
    {
        var key = NewKey();
        var handler = new CloudFakeHandler(HttpStatusCode.OK, AnthropicSse);
        var provider = Anthropic(handler);

        var tokens = await CollectAsync(provider, Request(key));

        tokens.Should().Equal("Hello", " world");
        provider.Name.Should().Be(ProviderNames.Anthropic);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/v1/messages");
        handler.LastRequest.Headers.GetValues("x-api-key").Should().Equal(key);
        handler.LastRequest.Headers.GetValues("anthropic-version").Should().Equal("2023-06-01");

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("the-model");
        root.GetProperty("max_tokens").GetInt32().Should().Be(1024);
        root.GetProperty("stream").GetBoolean().Should().BeTrue();
        root.GetProperty("temperature").GetDouble().Should().Be(0.3);
        root.GetProperty("system").GetString().Should().Be("rules\n\nmore rules");
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant", "user");
        messages[0].GetProperty("content").GetString().Should().Be("q1");
    }

    [Fact]
    public async Task Should_ExplainAnthropicError_When_StreamCarriesAnErrorEvent()
    {
        var handler = new CloudFakeHandler(HttpStatusCode.OK, "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}\n\n");

        var act = () => CollectAsync(Anthropic(handler), Request(NewKey()));

        await act.Should().ThrowAsync<LlmUnavailableException>().WithMessage("Anthropic reported an error*");
    }

    // ---- OpenAI ----

    private const string OpenAiSse = """
        data: {"choices":[{"delta":{"role":"assistant"}}]}

        data: {"choices":[{"delta":{"content":"Hi"}}]}

        data: {"choices":[]}

        data: {"choices":[{"delta":{"content":" there"}}]}

        data: [DONE]

        data: {"choices":[{"delta":{"content":"ignored"}}]}
        """;

    [Fact]
    public async Task Should_StreamOpenAiText_AndSendTheDocumentedRequest()
    {
        var key = NewKey();
        var handler = new CloudFakeHandler(HttpStatusCode.OK, OpenAiSse);
        var provider = OpenAi(handler);

        var tokens = await CollectAsync(provider, Request(key));

        tokens.Should().Equal("Hi", " there");
        provider.Name.Should().Be(ProviderNames.OpenAi);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(key);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("the-model");
        root.GetProperty("stream").GetBoolean().Should().BeTrue();
        root.GetProperty("temperature").GetDouble().Should().Be(0.3);
        root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString())
            .Should().Equal("system", "system", "user", "assistant", "user");
    }

    // ---- Google ----

    private const string GoogleSse = """
        data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hel"},{"text":"lo"}]}}]}

        data: {"candidates":[{"content":{"role":"model","parts":[{"text":" there"}]},"finishReason":"STOP"}]}

        data: {"usageMetadata":{"totalTokenCount":5}}

        """;

    [Fact]
    public async Task Should_StreamGeminiText_AndSendTheDocumentedRequest()
    {
        var key = NewKey();
        var handler = new CloudFakeHandler(HttpStatusCode.OK, GoogleSse);
        var provider = Google(handler);

        var tokens = await CollectAsync(provider, Request(key, "gemini-2.0-flash"));

        tokens.Should().Equal("Hello", " there");
        provider.Name.Should().Be(ProviderNames.Google);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1beta/models/gemini-2.0-flash:streamGenerateContent");
        handler.LastRequest.RequestUri.Query.Should().Be("?alt=sse");
        handler.LastRequest.Headers.GetValues("x-goog-api-key").Should().Equal(key);
        handler.LastRequest.RequestUri.ToString().Should().NotContain(key);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("rules\n\nmore rules");
        var contents = root.GetProperty("contents").EnumerateArray().ToList();
        contents.Select(c => c.GetProperty("role").GetString()).Should().Equal("user", "model", "user");
        contents[0].GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("q1");
        root.GetProperty("generationConfig").GetProperty("temperature").GetDouble().Should().Be(0.3);
    }

    [Fact]
    public async Task Should_OmitSystemInstruction_When_NoSystemMessage()
    {
        var handler = new CloudFakeHandler(HttpStatusCode.OK, GoogleSse);

        await CollectAsync(Google(handler), new LlmRequest("m", [new LlmMessage(LlmRole.User, "q")], 0.1, NewKey()));

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.TryGetProperty("systemInstruction", out _).Should().BeFalse();
    }

    // ---- Shared behaviour ----

    public static TheoryData<string> Providers => new() { "anthropic", "openai", "google" };

    private static ILlmProvider Build(string name, HttpMessageHandler handler) => name switch
    {
        "anthropic" => Anthropic(handler),
        "openai" => OpenAi(handler),
        _ => Google(handler),
    };

    private static string Display(string name) => name switch
    {
        "anthropic" => "Anthropic",
        "openai" => "OpenAI",
        _ => "Google Gemini",
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_AskForAKey_When_NoneWasSupplied(string name)
    {
        var handler = new CloudFakeHandler(HttpStatusCode.OK, "");

        var missing = () => CollectAsync(Build(name, handler), Request(null));
        var blank = () => CollectAsync(Build(name, handler), Request("  "));

        await missing.Should().ThrowAsync<LlmUnavailableException>().WithMessage($"Add your {Display(name)} API key in Settings.");
        await blank.Should().ThrowAsync<LlmUnavailableException>().WithMessage($"Add your {Display(name)} API key in Settings.");
        handler.LastRequest.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_MapRejectedKey_To_ActionableMessage(string name)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var key = NewKey();
            var handler = new CloudFakeHandler(status, $"{{\"error\":\"bad key {key}\"}}", "application/json");

            var act = () => CollectAsync(Build(name, handler), Request(key));

            var thrown = await act.Should().ThrowAsync<LlmUnavailableException>();
            thrown.Which.Message.Should().Be($"{Display(name)} rejected the API key.");
            thrown.Which.Message.Should().NotContain(key);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_MapRateLimit_To_ReadableMessage(string name)
    {
        var handler = new CloudFakeHandler(HttpStatusCode.TooManyRequests, "{}", "application/json");

        var act = () => CollectAsync(Build(name, handler), Request(NewKey()));

        await act.Should().ThrowAsync<LlmUnavailableException>()
            .WithMessage($"{Display(name)} is rate limiting requests. Try again shortly.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_GiveShortMessage_When_ProviderFails(string name)
    {
        var handler = new CloudFakeHandler(HttpStatusCode.InternalServerError, "boom", "text/plain");

        var act = () => CollectAsync(Build(name, handler), Request(NewKey()));

        await act.Should().ThrowAsync<LlmUnavailableException>().WithMessage($"{Display(name)} returned an error (500)*");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_KeepInnerException_When_ConnectionFails(string name)
    {
        var key = NewKey();
        var handler = new CloudFakeHandler(_ => throw new HttpRequestException("refused"));

        var act = () => CollectAsync(Build(name, handler), Request(key));

        var thrown = await act.Should().ThrowAsync<LlmUnavailableException>();
        thrown.Which.Message.Should().Be($"Can't reach {Display(name)}. Check your internet connection.");
        thrown.Which.InnerException.Should().BeOfType<HttpRequestException>();
        thrown.Which.ToString().Should().NotContain(key);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_ExplainUnreadableReply_When_StreamIsNotJson(string name)
    {
        var handler = new CloudFakeHandler(HttpStatusCode.OK, "data: {not json\n\n");

        var act = () => CollectAsync(Build(name, handler), Request(NewKey()));

        await act.Should().ThrowAsync<LlmUnavailableException>()
            .Where(e => e.InnerException is JsonException)
            .WithMessage("*couldn't be read*");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Should_StopTheHttpStream_When_CallerCancels(string name)
    {
        var stream = new BlockingStream(name switch
        {
            "anthropic" => "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"first\"}}\n\n",
            "openai" => "data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n",
            _ => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"first\"}]}}]}\n\n",
        });
        var handler = new CloudFakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var received = new List<string>();

        var act = async () =>
        {
            await foreach (var token in Build(name, handler).StreamAsync(Request(NewKey()), cts.Token))
            {
                received.Add(token);
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        received.Should().Equal("first");
        stream.Cancelled.Should().BeTrue();
    }

    [Fact]
    public void Should_RejectNullRequest()
    {
        var act = () => Anthropic(new CloudFakeHandler(HttpStatusCode.OK, "")).StreamAsync(null!, Ct);

        act.Should().Throw<ArgumentNullException>();
    }
}
