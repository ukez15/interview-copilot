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
    string? SessionPrompt);

public sealed record SetAutoAnswerRequest(bool Enabled);

internal static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions").RequireAuthorization();

        group.MapGet("/", (ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.ListAsync(user.UserId(), ct));

        group.MapPost("/", async (CreateSessionRequest request, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
        {
            var session = await sessions.CreateAsync(
                new CreateSessionCommand(
                    user.UserId(),
                    request.Title,
                    request.Mode,
                    request.AnswerLength,
                    request.Framework,
                    request.PinnedQuestionType,
                    request.SessionPrompt),
                ct);
            return TypedResults.Created($"/api/sessions/{session.Id}", session);
        });

        group.MapGet("/{sessionId:guid}", (Guid sessionId, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.GetAsync(user.UserId(), sessionId, ct));

        group.MapPut("/{sessionId:guid}/settings", (
            Guid sessionId,
            UpdateSessionSettingsCommand request,
            ClaimsPrincipal user,
            SessionService sessions,
            CancellationToken ct) => sessions.UpdateSettingsAsync(user.UserId(), sessionId, request, ct));

        group.MapPut("/{sessionId:guid}/auto-answer", (
            Guid sessionId,
            SetAutoAnswerRequest request,
            ClaimsPrincipal user,
            SessionService sessions,
            CancellationToken ct) => sessions.SetAutoAnswerAsync(user.UserId(), sessionId, request.Enabled, ct));

        group.MapPost("/{sessionId:guid}/start", (Guid sessionId, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.StartAsync(user.UserId(), sessionId, ct));

        group.MapPost("/{sessionId:guid}/end", (Guid sessionId, ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            sessions.EndAsync(user.UserId(), sessionId, ct));
    }
}
