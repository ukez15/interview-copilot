using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Sessions;

namespace InterviewCopilot.IntegrationTests;

public sealed class AudioStreamTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory;

    public AudioStreamTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, string Token, SessionDto Session)> LiveSessionAsync(string user, bool start = true)
    {
        var client = await _factory.CreateSignedInClientAsync(user);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var created = await (await client.PostAsJsonAsync("/api/sessions", new
        {
            Title = "Audio",
            Mode = "LiveInterview",
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

    private Task<WebSocket> ConnectAsync(Guid sessionId, string token) =>
        _factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"ws/sessions/{sessionId}/audio?access_token={Uri.EscapeDataString(token)}"),
            Ct);

    private static byte[] Frame(byte speaker) => [speaker, .. new byte[3_200]];

    [Fact]
    public async Task Should_AcceptFramesForBothSpeakers_And_ReportStats()
    {
        var (client, token, session) = await LiveSessionAsync("audio-ok");
        using var socket = await ConnectAsync(session.Id, token);

        await socket.SendAsync(Frame(0), WebSocketMessageType.Binary, true, Ct);
        await socket.SendAsync(Frame(0), WebSocketMessageType.Binary, true, Ct);
        await socket.SendAsync(Frame(1), WebSocketMessageType.Binary, true, Ct);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);

        var stats = await client.GetFromJsonAsync<AudioStatsDto>($"/api/sessions/{session.Id}/audio/stats", Json, Ct);
        stats!.InterviewerBytes.Should().Be(6_400);
        stats.CandidateBytes.Should().Be(3_200);
        stats.Frames.Should().Be(3);
    }

    [Fact]
    public async Task Should_CloseWith1003_When_FrameMalformed()
    {
        var (_, token, session) = await LiveSessionAsync("audio-bad");
        using var socket = await ConnectAsync(session.Id, token);

        await socket.SendAsync(new byte[] { 7, 0, 0 }, WebSocketMessageType.Binary, true, Ct);
        var result = await socket.ReceiveAsync(new byte[64], Ct);

        result.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.InvalidMessageType);
    }

    [Fact]
    public async Task Should_CloseWith1003_When_TextOrOversized()
    {
        var (_, token, session) = await LiveSessionAsync("audio-text");
        using var text = await ConnectAsync(session.Id, token);
        await text.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, Ct);
        await text.ReceiveAsync(new byte[64], Ct);

        using var big = await ConnectAsync(session.Id, token);
        await big.SendAsync(new byte[5_000], WebSocketMessageType.Binary, true, Ct);
        await big.ReceiveAsync(new byte[64], Ct);

        text.CloseStatus.Should().Be(WebSocketCloseStatus.InvalidMessageType);
        big.CloseStatus.Should().Be(WebSocketCloseStatus.InvalidMessageType);
    }

    [Fact]
    public async Task Should_RefuseUpgrade_When_SessionNotLive_OrTokenMissing_OrNotOwner()
    {
        var (_, token, draft) = await LiveSessionAsync("audio-draft", start: false);
        var (_, strangerToken, _) = await LiveSessionAsync("audio-stranger");

        var notLive = () => ConnectAsync(draft.Id, token);
        var noToken = () => ConnectAsync(draft.Id, "invalid");
        var notOwner = () => ConnectAsync(draft.Id, strangerToken);

        (await notLive.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*409*");
        (await noToken.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*401*");
        (await notOwner.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*404*");
    }

    [Fact]
    public async Task Should_Return400_When_NotAWebSocketRequest()
    {
        var (client, _, session) = await LiveSessionAsync("audio-http");

        var response = await client.GetAsync($"/ws/sessions/{session.Id}/audio", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
