using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Api.Endpoints;

internal static class StorageEndpoints
{
    public static void MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/storage", (IOptions<StorageOptions> storage) =>
            TypedResults.Ok(new StorageInfo(storage.Value.ResolveDataDirectory())))
        .WithTags("Storage")
        .RequireAuthorization();
    }

    /// <summary>Where this backend keeps its data on disk.</summary>
    /// <param name="DataDirectory">Absolute path of the data folder.</param>
    internal sealed record StorageInfo(string DataDirectory);
}
