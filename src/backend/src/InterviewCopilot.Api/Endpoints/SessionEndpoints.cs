using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Api.Endpoints;

public sealed record CreateSessionRequest(
    string Title,
    SessionMode Mode,
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string? SessionPrompt,
    Guid? CopyFromSessionId = null);

public sealed record SetAutoAnswerRequest(bool Enabled);

/// <summary>PUT /api/sessions/{id}/keys/{provider}: a BYOK key for this live session only (wave 3).</summary>
public sealed record SetSessionKeyRequest(string Key);

internal static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");

        // A paired browser may list sessions (#33), so this one doesn't inherit the bearer-only default below.
        group.MapGet("/", (ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
                sessions.ListAsync(user.UserId(), ct))
            .RequireAuthorization(Policies.ExtensionScope);

        var owner = group.MapGroup(string.Empty).RequireAuthorization();

        owner.MapPost("/", async (CreateSessionRequest request, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
        {
            var session = await sessions.CreateAsync(
                new CreateSessionCommand(
                    user.UserId(),
                    request.Title,
                    request.Mode,
                    request.AnswerLength,
                    request.Framework,
                    request.PinnedQuestionType,
                    request.SessionPrompt,
                    request.CopyFromSessionId),
                ct);
            return TypedResults.Created($"/api/sessions/{session.Id}", session);
        });

        owner.MapGet("/{sessionId:guid}", (Guid sessionId, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.GetAsync(user.UserId(), sessionId, ct));

        owner.MapPut("/{sessionId:guid}/settings", (
            Guid sessionId,
            UpdateSessionSettingsCommand request,
            ClaimsPrincipal user,
            SessionService sessions,
            CancellationToken ct) => sessions.UpdateSettingsAsync(user.UserId(), sessionId, request, ct));

        owner.MapPut("/{sessionId:guid}/auto-answer", (
            Guid sessionId,
            SetAutoAnswerRequest request,
            ClaimsPrincipal user,
            SessionService sessions,
            CancellationToken ct) => sessions.SetAutoAnswerAsync(user.UserId(), sessionId, request.Enabled, ct));

        owner.MapPost("/{sessionId:guid}/start", (Guid sessionId, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.StartAsync(user.UserId(), sessionId, ct));

        owner.MapPost("/{sessionId:guid}/end", (Guid sessionId, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.EndAsync(user.UserId(), sessionId, ct));

        owner.MapPut("/{sessionId:guid}/keys/{provider}", async (
            Guid sessionId,
            string provider,
            SetSessionKeyRequest request,
            ClaimsPrincipal user,
            SessionService sessions,
            CancellationToken ct) =>
        {
            await sessions.SetSessionKeyAsync(user.UserId(), sessionId, provider, request.Key, ct);
            return Results.NoContent();
        });
    }
}
