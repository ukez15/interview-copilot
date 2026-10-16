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

        MapWhisperModelEndpoints(app);

        app.MapGet("/api/providers/ollama/models", async (IOllamaModelCatalog catalog, CancellationToken ct) =>
            TypedResults.Ok(await catalog.ListInstalledAsync(ct)))
        .WithTags("Providers")
        .RequireAuthorization();
    }

    private static void MapWhisperModelEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers/whisper/model", (IWhisperModelStatus model) => TypedResults.Ok(ToResponse(model.GetStatus())))
            .WithTags("Providers")
            .RequireAuthorization();

        app.MapPost("/api/providers/whisper/model/download", (IWhisperModelStatus model) =>
        {
            var start = model.StartDownload();
            var body = ToResponse(model.GetStatus());
            return start switch
            {
                WhisperDownloadStart.AlreadyDownloading => Results.Conflict(body),
                WhisperDownloadStart.AlreadyReady => Results.Ok(body),
                _ => Results.Accepted("/api/providers/whisper/model", body),
            };
        })
        .WithTags("Providers")
        .RequireAuthorization();
    }

    private static object ToResponse(WhisperModelStatus status) => new
    {
        State = status.State.ToString().ToLowerInvariant(),
        status.Model,
        status.BytesDownloaded,
        status.TotalBytes,
        status.Message,
    };
}
