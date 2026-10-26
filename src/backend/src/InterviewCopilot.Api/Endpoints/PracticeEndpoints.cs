using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Practice;

namespace InterviewCopilot.Api.Endpoints;

internal static class PracticeEndpoints
{
    public static void MapPracticeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions/{sessionId:guid}/practice").WithTags("Practice").RequireAuthorization();

        group.MapPost("/next", (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
            practice.NextAsync(user.UserId(), sessionId, ct));

        group.MapPost("/finish", (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
            practice.FinishAsync(user.UserId(), sessionId, ct));

        group.MapGet("/attempts", (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
            practice.ListAsync(user.UserId(), sessionId, ct));
    }
}
