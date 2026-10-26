using System.Net.Http.Json;

namespace InterviewCopilot.Benchmark;

/// <summary>Asks one question over SSE and times it: request sent -> first <c>delta</c> (TTFT) -> <c>done</c> (total).</summary>
internal sealed class QuestionRunner(HttpClient http, IClock clock, TimeSpan timeout)
{
    private const int MaxErrorLength = 120;

    public async Task<QuestionResult> RunAsync(Guid sessionId, int number, Question question, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var start = clock.GetTimestamp();

        QuestionResult Fail(string error) => new(number, question.Type, null, null, error);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/answers")
            {
                Content = JsonContent.Create(new AskRequest(question.Text)),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"HTTP {(int)response.StatusCode}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            double? ttft = null;
            await foreach (var sse in SseParser.ReadEventsAsync(body, limit.Token))
            {
                switch (sse.Name)
                {
                    case "delta":
                        ttft ??= clock.GetElapsed(start).TotalMilliseconds;
                        break;
                    case "error":
                        return Fail($"model error: {Truncate(sse.Data)}");
                    case "done":
                        return ttft is null
                            ? Fail("stream finished with no tokens")
                            : new QuestionResult(number, question.Type, ttft, clock.GetElapsed(start).TotalMilliseconds, null);
                }
            }

            return Fail("stream ended before the done event");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail($"timed out after {timeout.TotalSeconds:0.###} s");
        }
        catch (HttpRequestException ex)
        {
            return Fail(Truncate(ex.Message));
        }
    }

    private static string Truncate(string text)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= MaxErrorLength ? singleLine : singleLine[..MaxErrorLength] + "...";
    }

    private sealed record AskRequest(string Question);
}
