using System.Net.ServerSentEvents;
using System.Security.Claims;
using System.Text.Json;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Practice;

namespace InterviewCopilot.Api.Endpoints;

internal static class PracticeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapPracticeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions/{sessionId:guid}/practice").WithTags("Practice").RequireAuthorization();

        // Streams many "delta" (the question as it's written), then "done" (the question and answerFromMs) or "error" (#78).
        group.MapPost("/next", async (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
        {
            var parts = await practice.NextAsync(user.UserId(), sessionId, ct);
            return TypedResults.ServerSentEvents(LlmEventStream.RelayAsync(parts, ToEvent, last: null, ct));
        });

        group.MapPost("/finish", (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
            practice.FinishAsync(user.UserId(), sessionId, ct));

        group.MapGet("/attempts", (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
            practice.ListAsync(user.UserId(), sessionId, ct));
    }

    private static SseItem<string> ToEvent(PracticeQuestionPart part) =>
        part.Question is { } question
            ? new SseItem<string>(JsonSerializer.Serialize(question, JsonOptions), LlmEventStream.Done)
            : new SseItem<string>(part.Delta, LlmEventStream.Delta);
}
