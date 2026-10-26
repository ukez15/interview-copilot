using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>One decoded server-sent event of a cloud chat stream.</summary>
/// <param name="Text">Text to append to the answer, if the event carried any.</param>
/// <param name="Done">True when the provider says the answer is complete.</param>
internal readonly record struct CloudChunk(string? Text, bool Done);

/// <summary>
/// Shared plumbing for the BYOK chat providers (#30): key check, streaming request, SSE reading and error mapping.
/// Messages are user-facing and never contain the API key (it only travels in a request header).
/// </summary>
internal abstract class CloudLlmProviderBase(HttpClient http) : ILlmProvider
{
    public abstract string Name { get; }

    /// <summary>Provider name as shown to the user in error messages.</summary>
    protected abstract string DisplayName { get; }

    public IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    protected abstract HttpRequestMessage BuildMessage(LlmRequest request, string apiKey);

    protected abstract CloudChunk Parse(string data);

    private async IAsyncEnumerable<string> StreamCoreAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new LlmUnavailableException(CloudProviderErrors.MissingKey(DisplayName));
        }

        using var message = BuildMessage(request, request.ApiKey);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException(CloudProviderErrors.Unreachable(DisplayName), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmUnavailableException(CloudProviderErrors.FromStatus(DisplayName, response.StatusCode));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }

                CloudChunk chunk;
                try
                {
                    chunk = Parse(data);
                }
                catch (JsonException ex)
                {
                    throw new LlmUnavailableException(CloudProviderErrors.Unreadable(DisplayName), ex);
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    yield return chunk.Text;
                }

                if (chunk.Done)
                {
                    yield break;
                }
            }
        }
    }

    protected static string RoleName(LlmRole role) => role switch
    {
        LlmRole.System => "system",
        LlmRole.Assistant => "assistant",
        _ => "user",
    };
}

/// <summary>User-facing messages for cloud providers. None of them may include the key.</summary>
internal static class CloudProviderErrors
{
    public static string MissingKey(string provider) => $"Add your {provider} API key in Settings.";

    public static string Rejected(string provider) => $"{provider} rejected the API key.";

    public static string RateLimited(string provider) => $"{provider} is rate limiting requests. Try again shortly.";

    public static string Unreachable(string provider) => $"Can't reach {provider}. Check your internet connection.";

    public static string Unreadable(string provider) => $"{provider} sent a reply that couldn't be read.";

    public static string FromStatus(string provider, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Rejected(provider),
        HttpStatusCode.TooManyRequests => RateLimited(provider),
        _ => $"{provider} returned an error ({(int)status}). Try again shortly.",
    };
}
