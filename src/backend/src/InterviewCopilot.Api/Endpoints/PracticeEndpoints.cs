using System.Globalization;
using System.Net.ServerSentEvents;
using System.Security.Claims;
using System.Text.Json;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Practice;

namespace InterviewCopilot.Api.Endpoints;

internal static class PracticeEndpoints
{
    private const string ScoreEvent = "score";
    private const string RestartEvent = "restart";

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

        // Streams "score" (once), many "delta" (the feedback as it's written), "restart" if the reply had to be asked
        // again, then "done" (the saved attempt) or "error" (#78). "No answer heard." is still a 400 before the stream.
        group.MapPost("/finish", async (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
        {
            var parts = await practice.FinishAsync(user.UserId(), sessionId, ct);
            return TypedResults.ServerSentEvents(LlmEventStream.RelayAsync(parts, ToEvent, last: null, ct));
        });

        group.MapGet("/attempts", (Guid sessionId, ClaimsPrincipal user, PracticeService practice, CancellationToken ct) =>
            practice.ListAsync(user.UserId(), sessionId, ct));
    }

    private static SseItem<string> ToEvent(PracticeScorePart part)
    {
        if (part.Attempt is { } attempt)
        {
            return new SseItem<string>(JsonSerializer.Serialize(attempt, JsonOptions), LlmEventStream.Done);
        }

        if (part.Score is { } score)
        {
            return new SseItem<string>(score.ToString(CultureInfo.InvariantCulture), ScoreEvent);
        }

        return part.Restart ? new SseItem<string>(string.Empty, RestartEvent) : new SseItem<string>(part.Delta, LlmEventStream.Delta);
    }

    private static SseItem<string> ToEvent(PracticeQuestionPart part) =>
        part.Question is { } question
            ? new SseItem<string>(JsonSerializer.Serialize(question, JsonOptions), LlmEventStream.Done)
            : new SseItem<string>(part.Delta, LlmEventStream.Delta);
}
