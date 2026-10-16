using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Api.Endpoints;

internal static class ProviderEndpoints
{
    public static void MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers", (ProviderCatalog catalog) => TypedResults.Ok(new
        {
            SpeechToText = catalog.SpeechToTextProviders(),
            Llm = catalog.LlmProviders(),
        }))
        .WithTags("Providers")
        .RequireAuthorization();

        app.MapGet("/api/providers/ollama/models", async (IOllamaModelCatalog catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.ListInstalledAsync(ct)))
        .WithTags("Providers")
        .RequireAuthorization();
    }
}
