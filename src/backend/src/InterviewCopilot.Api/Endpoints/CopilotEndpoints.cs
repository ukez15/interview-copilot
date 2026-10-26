using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace InterviewCopilot.Api.Endpoints;

public sealed record AnswerQuestionRequest(
    string Question,
    AnswerModifier Modifier = AnswerModifier.None,
    string? PreviousAnswer = null,
    int PreviousVersion = 0);

internal static class CopilotEndpoints
{
    /// <summary>BYOK key header. The key is used for this request only; never logged or stored.</summary>
    public const string LlmApiKeyHeader = "X-Llm-Api-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapCopilotEndpoints(this IEndpointRouteBuilder app)
    {
        // Streams: "meta" (type + version), many "delta" (tokens), then "done" or "error" (FR-09, FR-10).
        app.MapPost("/api/sessions/{sessionId:guid}/answers", async (
                Guid sessionId,
                AnswerQuestionRequest request,
                [FromHeader(Name = LlmApiKeyHeader)] string? apiKey,
                ClaimsPrincipal user,
                SessionService sessions,
                AnswerGenerator generator,
                CancellationToken ct) =>
            {
                await sessions.GetAsync(user.UserId(), sessionId, ct); // ownership check, 404 otherwise
                var (header, tokens) = await generator.GenerateAsync(
                    new AnswerRequest(sessionId, request.Question, request.Modifier, request.PreviousAnswer, request.PreviousVersion, apiKey),
                    ct);
                return TypedResults.ServerSentEvents(ToEvents(header, tokens, ct));
            })
            .WithTags("Copilot")
            .RequireAuthorization(Policies.ExtensionScope);
    }

    private static async IAsyncEnumerable<SseItem<string>> ToEvents(
        AnswerHeader header,
        IAsyncEnumerable<string> tokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SseItem<string>(JsonSerializer.Serialize(header, JsonOptions), "meta");

        var enumerator = tokens.GetAsyncEnumerator(cancellationToken);
        string? error = null;
        try
        {
            while (true)
            {
                string token;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    token = enumerator.Current;
                }
                catch (LlmUnavailableException ex)
                {
                    error = ex.Message;
                    break;
                }

                yield return new SseItem<string>(token, "delta");
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        yield return error is null
            ? new SseItem<string>(string.Empty, "done")
            : new SseItem<string>(error, "error");
    }
}
