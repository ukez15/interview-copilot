using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>Reads the installed models from Ollama's <c>GET api/tags</c>.</summary>
internal sealed class OllamaModelCatalog : IOllamaModelCatalog
{
    private const string Unreachable = "Can't reach Ollama. Make sure it's installed and running.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public OllamaModelCatalog(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("api/tags", cancellationToken);
            response.EnsureSuccessStatusCode();
            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(JsonOptions, cancellationToken);
            return [.. (tags?.Models ?? []).Select(m => new OllamaModelInfo(m.Name, m.Size, m.ModifiedAt))];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new LlmUnavailableException(Unreachable, ex);
        }
    }

    private sealed record TagsResponse(IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(
        string Name,
        long Size,
        [property: JsonPropertyName("modified_at")] DateTimeOffset ModifiedAt);
}
