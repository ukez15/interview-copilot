using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Preferences;

namespace InterviewCopilot.Api.Endpoints;

public sealed record UpdatePreferencesRequest(string? LlmModel);

/// <summary>PUT /api/preferences/llm: provider and model together (#30).</summary>
public sealed record UpdateLlmPreferenceRequest(string? Provider, string? Model);

/// <summary>PUT /api/preferences/speech-to-text (#20).</summary>
public sealed record UpdateSpeechToTextPreferenceRequest(string? Provider);

internal static class PreferencesEndpoints
{
    public static void MapPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").WithTags("Preferences").RequireAuthorization();

        group.MapGet("/", (ClaimsPrincipal user, PreferencesService preferences, CancellationToken ct) =>
            preferences.GetAsync(user.UserId(), ct));

        group.MapPut("/", (UpdatePreferencesRequest request, ClaimsPrincipal user, PreferencesService preferences, CancellationToken ct) =>
            preferences.SetLlmModelAsync(user.UserId(), request.LlmModel, ct));

        group.MapPut("/llm", (UpdateLlmPreferenceRequest request, ClaimsPrincipal user, PreferencesService preferences, CancellationToken ct) =>
            preferences.SetLlmAsync(user.UserId(), request.Provider, request.Model, ct));

        group.MapPut("/speech-to-text", (
                UpdateSpeechToTextPreferenceRequest request,
                ClaimsPrincipal user,
                PreferencesService preferences,
                CancellationToken ct) =>
            preferences.SetSpeechToTextProviderAsync(user.UserId(), request.Provider, ct));
    }
}
