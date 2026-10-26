using System.Net;
using System.Text.Json;
using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>Shared plumbing for listing a cloud provider's models with a BYOK key (#30).</summary>
internal abstract class CloudModelCatalogBase(HttpClient http) : ICloudModelCatalog
{
    public abstract string Provider { get; }

    protected abstract string DisplayName { get; }

    public async Task<IReadOnlyList<CloudModelInfo>> ListAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        try
        {
            using var message = BuildMessage(apiKey);
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new LlmKeyRejectedException(CloudProviderErrors.Rejected(DisplayName))
                    : new LlmUnavailableException(CloudProviderErrors.FromStatus(DisplayName, response.StatusCode));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return [.. Parse(document.RootElement).OrderBy(m => m.Id, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new LlmUnavailableException(CloudProviderErrors.Unreachable(DisplayName), ex);
        }
        catch (JsonException ex)
        {
            throw new LlmUnavailableException(CloudProviderErrors.Unreadable(DisplayName), ex);
        }
    }

    protected abstract HttpRequestMessage BuildMessage(string apiKey);

    protected abstract IEnumerable<CloudModelInfo> Parse(JsonElement root);

    protected static IEnumerable<JsonElement> Items(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(property, out var list)
            && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [];

    protected static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
