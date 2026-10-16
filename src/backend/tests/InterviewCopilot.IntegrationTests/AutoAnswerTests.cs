using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Sessions;

namespace InterviewCopilot.IntegrationTests;

public sealed class AutoAnswerTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory;

    public AutoAnswerTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, string Token, SessionDto Session)> LiveSessionAsync(string user)
    {
        var client = await _factory.CreateSignedInClientAsync(user);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var created = await (await client.PostAsJsonAsync("/api/sessions", new
        {
            Title = "Auto",
            Mode = "LiveInterview",
            AnswerLength = "Short",
            Framework = "None",
            PinnedQuestionType = (string?)null,
            SessionPrompt = "",
        }, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        (await client.PostAsync($"/api/sessions/{created!.Id}/start", null, Ct)).EnsureSuccessStatusCode();
        return (client, token, created);
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

    private static async Task<JsonElement> ReadFrameAsync(WebSocket socket)
    {
        var buffer = new byte[4_096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
    }

    private static async Task<List<string>> ReadTypesUntilAsync(WebSocket socket, string lastType)
    {
        var types = new List<string>();
        while (true)
        {
            var frame = await ReadFrameAsync(socket);
            var type = frame.GetProperty("type").GetString()!;
            types.Add(type);
            if (type == lastType)
            {
                return types;
            }
        }
    }

    [Fact]
    public async Task Should_StreamQuestionDetectedThenAnswer_When_InterviewerAsksOverAudio()
    {
        _factory.Stt.Text = "Tell me about a time you led a team.";
        _factory.Llm.Tokens = ["One ", "two."];
        var (_, token, session) = await LiveSessionAsync("auto-flow");
        using var events = await ConnectAsync("events", session.Id, token);
        using var audio = await ConnectAsync("audio", session.Id, token);

        await SpeakAsync(audio, 0);
        var types = await ReadTypesUntilAsync(events, "answer.done");
        await audio.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);

        types.Should().StartWith(["transcript.segment", "question.detected", "answer.delta"]);
        types.Should().EndWith("answer.done");
    }

    [Fact]
    public async Task Should_TogglePauseOverHttp_AndPublishState()
    {
        var (client, token, session) = await LiveSessionAsync("auto-toggle");
        using var events = await ConnectAsync("events", session.Id, token);

        var response = await client.PutAsJsonAsync($"/api/sessions/{session.Id}/auto-answer", new { Enabled = false }, Ct);
        var frame = await ReadFrameAsync(events);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SessionDto>(Json, Ct))!.AutoAnswer.Should().BeFalse();
        frame.GetProperty("type").GetString().Should().Be("autoanswer.state");
        frame.GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Should_Return404_When_ToggleOtherUsersSession()
    {
        var (_, _, session) = await LiveSessionAsync("auto-owner");
        var (stranger, _, _) = await LiveSessionAsync("auto-stranger");

        var response = await stranger.PutAsJsonAsync($"/api/sessions/{session.Id}/auto-answer", new { Enabled = false }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
