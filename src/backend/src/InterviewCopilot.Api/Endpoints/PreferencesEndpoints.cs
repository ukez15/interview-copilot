using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Preferences;

namespace InterviewCopilot.Api.Endpoints;

public sealed record UpdatePreferencesRequest(string? LlmModel);

internal static class PreferencesEndpoints
{
    public static void MapPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").WithTags("Preferences").RequireAuthorization();

        group.MapGet("/", (ClaimsPrincipal user, PreferencesService preferences, CancellationToken ct) =>
            preferences.GetAsync(user.UserId(), ct));

        group.MapPut("/", (UpdatePreferencesRequest request, ClaimsPrincipal user, PreferencesService preferences, CancellationToken ct) =>
            preferences.SetLlmModelAsync(user.UserId(), request.LlmModel, ct));
    }
}
