using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Sessions;

namespace InterviewCopilot.IntegrationTests;

public sealed class SessionEventsSocketTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory;

    public SessionEventsSocketTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, string Token, SessionDto Session)> SessionAsync(string user)
    {
        var client = await _factory.CreateSignedInClientAsync(user);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var created = await (await client.PostAsJsonAsync("/api/sessions", new
        {
            Title = "Events",
            Mode = "LiveInterview",
            AnswerLength = "Short",
            Framework = "None",
            PinnedQuestionType = (string?)null,
            SessionPrompt = "",
        }, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        return (client, token, created!);
    }

    private Task<WebSocket> ConnectAsync(Guid sessionId, string token) =>
        _factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"ws/sessions/{sessionId}/events?access_token={Uri.EscapeDataString(token)}"),
            Ct);

    private static async Task<List<JsonElement>> ReadUntilDoneAsync(WebSocket socket)
    {
        var frames = new List<JsonElement>();
        var buffer = new byte[4_096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, Ct);
            result.MessageType.Should().Be(WebSocketMessageType.Text);
            var frame = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
            frames.Add(frame);
            if (frame.GetProperty("type").GetString() == "answer.done")
            {
                return frames;
            }
        }
    }

    [Fact]
    public async Task Should_DeliverEveryAnswerDeltaInOrder_ToBothClients()
    {
        var (client, token, session) = await SessionAsync("events-two");
        _factory.Llm.Tokens = ["One ", "two ", "three."];
        using var first = await ConnectAsync(session.Id, token);
        using var second = await ConnectAsync(session.Id, token);

        var response = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/answers", new { Question = "Tell me about a time you led a team." }, Ct);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync(Ct);

        foreach (var socket in new[] { first, second })
        {
            var frames = await ReadUntilDoneAsync(socket);
            frames.Select(f => f.GetProperty("id").GetInt64()).Should().Equal(1, 2, 3, 4);
            frames.Take(3).Select(f => f.GetProperty("type").GetString()).Should().OnlyContain(t => t == "answer.delta");
            frames.Take(3).Select(f => f.GetProperty("data").GetProperty("token").GetString()).Should().Equal("One ", "two ", "three.");
            frames[0].GetProperty("data").GetProperty("version").GetInt32().Should().Be(1);
            frames[0].GetProperty("data").GetProperty("questionType").GetString().Should().Be("Behavioral");
            frames[3].GetProperty("data").GetProperty("answerId").GetGuid().Should().NotBeEmpty();
        }

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Fact]
    public async Task Should_KeepStreaming_ToRemainingClient_AfterOneDisconnects()
    {
        var (client, token, session) = await SessionAsync("events-drop");
        using var leaving = await ConnectAsync(session.Id, token);
        using var staying = await ConnectAsync(session.Id, token);
        await leaving.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);

        var response = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/answers", new { Question = "Why us?" }, Ct);
        await response.Content.ReadAsStringAsync(Ct);

        var frames = await ReadUntilDoneAsync(staying);
        frames[^1].GetProperty("type").GetString().Should().Be("answer.done");
    }

    [Fact]
    public async Task Should_Return404_When_NotOwner_OrNoToken401()
    {
        var (_, _, session) = await SessionAsync("events-owner");
        var (_, strangerToken, _) = await SessionAsync("events-stranger");

        var notOwner = () => ConnectAsync(session.Id, strangerToken);
        var noToken = () => ConnectAsync(session.Id, "invalid");

        (await notOwner.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*404*");
        (await noToken.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*401*");
    }

    [Fact]
    public async Task Should_Return400_When_NotAWebSocketRequest()
    {
        var (client, _, session) = await SessionAsync("events-http");

        var response = await client.GetAsync($"/ws/sessions/{session.Id}/events", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
