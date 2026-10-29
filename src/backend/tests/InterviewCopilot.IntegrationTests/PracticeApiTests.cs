using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Application.Transcripts;

namespace InterviewCopilot.IntegrationTests;

public sealed class PracticeApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory;

    public PracticeApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, string Token, SessionDto Session)> SessionAsync(string user, string mode, bool start)
    {
        var client = await _factory.CreateSignedInClientAsync(user);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var created = await (await client.PostAsJsonAsync("/api/sessions", new
        {
            Title = "Practice",
            Mode = mode,
            AnswerLength = "Short",
            Framework = "None",
            PinnedQuestionType = (string?)null,
            SessionPrompt = "",
        }, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        if (start)
        {
            (await client.PostAsync($"/api/sessions/{created!.Id}/start", null, Ct)).EnsureSuccessStatusCode();
        }

        return (client, token, created!);
    }

    private Task<WebSocket> ConnectAsync(string channel, Guid sessionId, string token) =>
        _factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"ws/sessions/{sessionId}/{channel}?access_token={Uri.EscapeDataString(token)}"),
            Ct);

    private static byte[] Frame(byte speaker, bool loud)
    {
        var frame = new byte[1 + 3_200];
        frame[0] = speaker;
        if (loud)
        {
            for (var i = 1; i < frame.Length; i += 2)
            {
                frame[i] = 0x40;
                frame[i + 1] = 0x1F;
            }
        }

        return frame;
    }

    private static async Task SpeakAsync(WebSocket audio, byte speaker)
    {
        for (var i = 0; i < 5; i++)
        {
            await audio.SendAsync(Frame(speaker, true), WebSocketMessageType.Binary, true, Ct);
        }

        for (var i = 0; i < 6; i++)
        {
            await audio.SendAsync(Frame(speaker, false), WebSocketMessageType.Binary, true, Ct);
        }
    }

    private static async Task WaitForSegmentAsync(WebSocket events)
    {
        var buffer = new byte[4_096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = await events.ReceiveAsync(buffer, timeout.Token);
            var type = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.GetProperty("type").GetString();
            if (type == "transcript.segment")
            {
                return;
            }
        }
    }

    /// <summary>The segment event is published just before listeners run, so retry while the answer is still empty.</summary>
    private static async Task<HttpResponseMessage> FinishAsync(HttpClient client, Guid sessionId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var response = await client.PostAsync($"/api/sessions/{sessionId}/practice/finish", null, Ct);
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                return response;
            }

            await Task.Delay(50, Ct);
        }

        return await client.PostAsync($"/api/sessions/{sessionId}/practice/finish", null, Ct);
    }

    /// <summary>What the stream's "done" event carries: the question, or the saved attempt (#78).</summary>
    private static T Done<T>(string stream)
    {
        const string marker = "event: done\ndata: ";
        var start = stream.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the stream ends with a done event");
        var data = stream[(start + marker.Length)..].Split('\n')[0];
        return JsonSerializer.Deserialize<T>(data, Json)!;
    }

    private static PracticeQuestionDto DoneQuestion(string stream) => Done<PracticeQuestionDto>(stream);

    [Fact]
    public async Task Should_AskListenScoreAndSave_AndIncludeAttemptsInTranscript()
    {
        var (client, token, session) = await SessionAsync("practice-flow", "MockInterview", start: true);
        _factory.Llm.Tokens = ["What is dependency ", "injection?"];

        var next = await client.PostAsync($"/api/sessions/{session.Id}/practice/next", null, Ct);
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        next.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var stream = await next.Content.ReadAsStringAsync(Ct);
        stream.Should().Contain("event: delta").And.Contain("data: What is dependency ");
        DoneQuestion(stream).Question.Should().Be("What is dependency injection?");

        _factory.Stt.Text = "I pass collaborators through the constructor.";
        using var events = await ConnectAsync("events", session.Id, token);
        using var audio = await ConnectAsync("audio", session.Id, token);
        await SpeakAsync(audio, 1);
        await WaitForSegmentAsync(events);

        _factory.Llm.Tokens = ["SCORE: 7\n", "FEEDBACK: Mention testing."];
        var finish = await FinishAsync(client, session.Id);
        await audio.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);

        finish.StatusCode.Should().Be(HttpStatusCode.OK);
        finish.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var scored = await finish.Content.ReadAsStringAsync(Ct);
        scored.Should().Contain("event: score\ndata: 7").And.Contain("event: delta\ndata: Mention testing.");
        var attempt = Done<PracticeAttemptDto>(scored);
        attempt.Score.Should().Be(7);
        attempt.Feedback.Should().Be("Mention testing.");
        attempt.Question.Should().Be("What is dependency injection?");
        attempt.AnswerText.Should().Be("I pass collaborators through the constructor.");

        var list = await client.GetFromJsonAsync<List<PracticeAttemptDto>>($"/api/sessions/{session.Id}/practice/attempts", Json, Ct);
        list.Should().ContainSingle().Which.Id.Should().Be(attempt.Id);
        var transcript = await client.GetFromJsonAsync<TranscriptDto>($"/api/sessions/{session.Id}/transcript", Json, Ct);
        transcript!.Attempts.Should().ContainSingle().Which.Score.Should().Be(7);
        (await client.GetAsync($"/api/sessions/{session.Id}/transcript/pdf", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_Return400AndSaveNothing_When_NoAnswerWasHeard()
    {
        var (client, _, session) = await SessionAsync("practice-empty", "MockInterview", start: true);
        _factory.Llm.Tokens = ["Why this role?"];
        (await client.PostAsync($"/api/sessions/{session.Id}/practice/next", null, Ct)).EnsureSuccessStatusCode();

        var finish = await client.PostAsync($"/api/sessions/{session.Id}/practice/finish", null, Ct);

        finish.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await finish.Content.ReadAsStringAsync(Ct)).Should().Contain("No answer heard.");
        (await client.GetFromJsonAsync<List<PracticeAttemptDto>>($"/api/sessions/{session.Id}/practice/attempts", Json, Ct))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Return400_When_SessionIsNotAMockInterview()
    {
        var (client, _, session) = await SessionAsync("practice-live-mode", "LiveInterview", start: true);

        var next = await client.PostAsync($"/api/sessions/{session.Id}/practice/next", null, Ct);
        var attempts = await client.GetAsync($"/api/sessions/{session.Id}/practice/attempts", Ct);

        next.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        attempts.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Should_Return409_When_SessionIsNotLive()
    {
        var (client, _, session) = await SessionAsync("practice-not-live", "MockInterview", start: false);

        var next = await client.PostAsync($"/api/sessions/{session.Id}/practice/next", null, Ct);
        var finish = await client.PostAsync($"/api/sessions/{session.Id}/practice/finish", null, Ct);

        next.StatusCode.Should().Be(HttpStatusCode.Conflict);
        finish.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Should_Return404_ForOtherUsersSession_And401_WhenAnonymous()
    {
        var (_, _, session) = await SessionAsync("practice-owner", "MockInterview", start: true);
        var (stranger, _, _) = await SessionAsync("practice-stranger", "MockInterview", start: false);

        var other = await stranger.PostAsync($"/api/sessions/{session.Id}/practice/next", null, Ct);
        var anonymous = await _factory.CreateClient().GetAsync($"/api/sessions/{session.Id}/practice/attempts", Ct);

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_StreamAnError_AndOpenNoQuestion_When_ModelIsUnavailable()
    {
        var (client, _, session) = await SessionAsync("practice-llm-down", "MockInterview", start: true);
        _factory.Llm.Fail = true;
        try
        {
            var next = await client.PostAsync($"/api/sessions/{session.Id}/practice/next", null, Ct);
            var stream = await next.Content.ReadAsStringAsync(Ct);

            stream.Should().Contain("event: error").And.NotContain("event: done");
            (await client.PostAsync($"/api/sessions/{session.Id}/practice/finish", null, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest); // no question was opened
        }
        finally
        {
            _factory.Llm.Fail = false;
        }
    }
}
