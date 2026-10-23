using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>Anthropic Messages API, streamed over SSE (BYOK, opt-in; #30).</summary>
internal sealed class AnthropicLlmProvider(HttpClient http) : CloudLlmProviderBase(http)
{
    internal const string ApiVersion = "2023-06-01";
    internal const int MaxTokens = 1024;

    public override string Name => ProviderNames.Anthropic;

    protected override string DisplayName => "Anthropic";

    protected override HttpRequestMessage BuildMessage(LlmRequest request, string apiKey)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == LlmRole.System).Select(m => m.Content));
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = new JsonArray([.. request.Messages
                .Where(m => m.Role != LlmRole.System)
                .Select(m => (JsonNode)new JsonObject { ["role"] = RoleName(m.Role), ["content"] = m.Content })]),
            ["temperature"] = request.Temperature,
            ["stream"] = true,
        };

        var message = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = JsonContent.Create(body) };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        return message;
    }

    protected override CloudChunk Parse(string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "content_block_delta":
                return root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text)
                    ? new CloudChunk(text.GetString(), false)
                    : default;
            case "message_stop":
                return new CloudChunk(null, true);
            case "error":
                throw new LlmUnavailableException("Anthropic reported an error while answering. Try again shortly.");
            default:
                return default;
        }
    }
}

/// <summary>Anthropic <c>GET v1/models</c> with the user's key.</summary>
internal sealed class AnthropicModelCatalog(HttpClient http) : CloudModelCatalogBase(http)
{
    public override string Provider => ProviderNames.Anthropic;

    protected override string DisplayName => "Anthropic";

    protected override HttpRequestMessage BuildMessage(string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "v1/models?limit=1000");
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", AnthropicLlmProvider.ApiVersion);
        return message;
    }

    protected override IEnumerable<CloudModelInfo> Parse(JsonElement root) =>
        Items(root, "data")
            .Select(item => (Id: Text(item, "id"), Name: Text(item, "display_name")))
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new CloudModelInfo(m.Id!, string.IsNullOrWhiteSpace(m.Name) ? m.Id! : m.Name));
}
