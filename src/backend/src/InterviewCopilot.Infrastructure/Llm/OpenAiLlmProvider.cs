using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>OpenAI Chat Completions, streamed over SSE (BYOK, opt-in; #30).</summary>
internal sealed class OpenAiLlmProvider(HttpClient http) : CloudLlmProviderBase(http)
{
    public override string Name => ProviderNames.OpenAi;

    protected override string DisplayName => "OpenAI";

    protected override HttpRequestMessage BuildMessage(LlmRequest request, string apiKey)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray([.. request.Messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = RoleName(m.Role), ["content"] = m.Content })]),
            ["temperature"] = request.Temperature,
            ["stream"] = true,
        };

        var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    protected override CloudChunk Parse(string data)
    {
        if (data == "[DONE]")
        {
            return new CloudChunk(null, true);
        }

        using var document = JsonDocument.Parse(data);
        if (document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return new CloudChunk(content.GetString(), false);
        }

        return default;
    }
}

/// <summary>OpenAI <c>GET v1/models</c> with the user's key. The API has no display names, so the id is used.</summary>
internal sealed class OpenAiModelCatalog(HttpClient http) : CloudModelCatalogBase(http)
{
    public override string Provider => ProviderNames.OpenAi;

    protected override string DisplayName => "OpenAI";

    protected override HttpRequestMessage BuildMessage(string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    protected override IEnumerable<CloudModelInfo> Parse(JsonElement root) =>
        Items(root, "data")
            .Select(item => Text(item, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new CloudModelInfo(id!, id!));
}
