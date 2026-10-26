using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Infrastructure;

/// <summary>The real <c>ClientWebSocket</c> adapter against a loopback server that behaves like Deepgram.</summary>
public sealed class DeepgramSocketTests : IAsyncDisposable
{
    private readonly string _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly List<(string Kind, byte[] Payload)> _received = [];
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private string? _authorization;
    private string? _query;
    private string? _path;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _release.TrySetResult();
        if (_app is not null)
        {
            await _app.StopAsync(Ct);
            await _app.DisposeAsync();
        }
    }

    private async Task<ClientWebSocketDeepgramConnectionFactory> StartAsync(
        Func<WebSocket, Task> serve,
        int? rejectWith = null,
        bool enabled = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.UseWebSockets();
        _app.Run(async context =>
        {
            _authorization = context.Request.Headers.Authorization;
            _query = context.Request.QueryString.Value;
            _path = context.Request.Path;
            if (rejectWith is { } status)
            {
                context.Response.StatusCode = status;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await serve(socket);
        });
        await _app.StartAsync(Ct);
        var options = new TranscriptionOptions();
        options.Providers.Deepgram.Enabled = enabled;
        options.Providers.Deepgram.BaseUrl = new Uri(_app.Urls.First());
        options.Providers.Deepgram.Model = "nova-3";
        return new ClientWebSocketDeepgramConnectionFactory(Options.Create(options));
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReadAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, Ct);
        return (result.MessageType, buffer[..result.Count]);
    }

    [Fact]
    public void Should_BuildTheWssListenUrl_FromTheConfiguredBaseAddress()
    {
        var deepgram = new DeepgramOptions { BaseUrl = new Uri("https://api.deepgram.com/"), Model = "nova-3" };

        var uri = ClientWebSocketDeepgramConnectionFactory.BuildUri(deepgram);

        uri.Scheme.Should().Be("wss");
        uri.Host.Should().Be("api.deepgram.com");
        uri.AbsolutePath.Should().Be("/v1/listen");
        uri.Query.Should().Be("?encoding=linear16&sample_rate=16000&channels=1&model=nova-3&interim_results=false&punctuate=true");
    }

    [Fact]
    public void Should_UsePlainWs_ForAnHttpBaseAddressAndKeepItsPath()
    {
        var deepgram = new DeepgramOptions { BaseUrl = new Uri("http://127.0.0.1:9000/proxy/"), Model = "a b" };

        var uri = ClientWebSocketDeepgramConnectionFactory.BuildUri(deepgram);

        uri.Scheme.Should().Be("ws");
        uri.AbsolutePath.Should().Be("/proxy/v1/listen");
        uri.Query.Should().Contain("model=a%20b");
    }

    [Fact]
    public async Task Should_AuthenticateSendAudioAndControlMessages_AndReadResults()
    {
        var factory = await StartAsync(async socket =>
        {
            for (var i = 0; i < 3; i++)
            {
                var (type, payload) = await ReadAsync(socket);
                _received.Add((type.ToString(), payload));
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"Results"}"""), WebSocketMessageType.Text, true, Ct);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
        });

        await using var connection = await factory.ConnectAsync(_apiKey, Ct);
        await connection.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, Ct);
        await connection.SendKeepAliveAsync(Ct);
        await connection.SendCloseStreamAsync(Ct);
        var message = await connection.ReceiveAsync(Ct);
        var closed = await connection.ReceiveAsync(Ct);

        message.Should().Be("""{"type":"Results"}""");
        closed.Should().BeNull();
        _authorization.Should().Be($"Token {_apiKey}");
        _path.Should().Be("/v1/listen");
        _query.Should().Contain("encoding=linear16").And.Contain("sample_rate=16000").And.Contain("interim_results=false");
        _received[0].Kind.Should().Be("Binary");
        _received[0].Payload.Should().Equal(1, 2, 3, 4);
        _received[1].Kind.Should().Be("Text");
        Encoding.UTF8.GetString(_received[1].Payload).Should().Be("""{"type":"KeepAlive"}""");
        Encoding.UTF8.GetString(_received[2].Payload).Should().Be("""{"type":"CloseStream"}""");
    }

    [Fact]
    public async Task Should_ReassembleAMessageSplitAcrossFrames()
    {
        var factory = await StartAsync(async socket =>
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"Res"""), WebSocketMessageType.Text, false, Ct);
            await socket.SendAsync(Encoding.UTF8.GetBytes("""ults"}"""), WebSocketMessageType.Text, true, Ct);
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        });
        await using var connection = await factory.ConnectAsync(_apiKey, Ct);

        (await connection.ReceiveAsync(Ct)).Should().Be("""{"type":"Results"}""");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Should_ThrowRejectedKey_WithoutTheKey_When_ServerRefusesTheUpgrade(int status)
    {
        var factory = await StartAsync(_ => Task.CompletedTask, rejectWith: status);

        var act = async () => await factory.ConnectAsync(_apiKey, Ct);

        var failure = (await act.Should().ThrowAsync<DeepgramRejectedKeyException>()).Which;
        failure.Message.Should().Be("Deepgram rejected the API key.");
        failure.ToString().Should().NotContain(_apiKey);
    }

    [Fact]
    public async Task Should_ThrowConnectionException_When_ServerAnswersSomethingElse()
    {
        var factory = await StartAsync(_ => Task.CompletedTask, rejectWith: 500);

        var act = async () => await factory.ConnectAsync(_apiKey, Ct);

        var failure = (await act.Should().ThrowAsync<DeepgramConnectionException>()).Which;
        failure.Should().NotBeOfType<DeepgramRejectedKeyException>();
        failure.ToString().Should().NotContain(_apiKey);
    }

    [Fact]
    public async Task Should_ThrowConnectionException_When_NothingListens()
    {
        var options = new TranscriptionOptions();
        options.Providers.Deepgram.Enabled = true;
        options.Providers.Deepgram.BaseUrl = new Uri("http://127.0.0.1:1/");
        var factory = new ClientWebSocketDeepgramConnectionFactory(Options.Create(options));

        var act = async () => await factory.ConnectAsync(_apiKey, Ct);

        await act.Should().ThrowAsync<DeepgramConnectionException>();
    }

    [Fact]
    public async Task Should_RefuseToConnect_When_DeepgramIsDisabled()
    {
        var factory = await StartAsync(_ => Task.CompletedTask, enabled: false);

        var act = async () => await factory.ConnectAsync(_apiKey, Ct);

        await act.Should().ThrowAsync<DeepgramConnectionException>().WithMessage("*disabled*");
        _authorization.Should().BeNull();
    }

    [Fact]
    public async Task Should_RequireAKey()
    {
        var factory = await StartAsync(_ => Task.CompletedTask);

        var act = async () => await factory.ConnectAsync(" ", Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Should_ThrowConnectionException_When_ServerClosesWithAnError()
    {
        var factory = await StartAsync(async socket =>
        {
            await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "oops", Ct);
        });
        await using var connection = await factory.ConnectAsync(_apiKey, Ct);

        var act = async () => await connection.ReceiveAsync(Ct);

        await act.Should().ThrowAsync<DeepgramConnectionException>();
    }

    [Fact]
    public async Task Should_ThrowConnectionException_When_ServerDropsTheSocket()
    {
        var factory = await StartAsync(socket =>
        {
            socket.Abort();
            return Task.CompletedTask;
        });

        // The abort can land during the handshake or after it; either way the caller sees a connection error.
        var act = async () =>
        {
            await using var connection = await factory.ConnectAsync(_apiKey, Ct);
            while (true)
            {
                await connection.ReceiveAsync(Ct);
            }
        };

        await act.Should().ThrowAsync<DeepgramConnectionException>();
    }

    [Fact]
    public async Task Should_ThrowConnectionException_When_SendingOnADisposedSocket()
    {
        var factory = await StartAsync(async _ => await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var connection = await factory.ConnectAsync(_apiKey, Ct);
        await connection.DisposeAsync();

        var act = async () => await connection.SendAudioAsync(new byte[] { 1, 2 }, Ct);

        await act.Should().ThrowAsync<DeepgramConnectionException>();
    }

    [Fact]
    public async Task Should_DisposeTwiceWithoutFailing()
    {
        var factory = await StartAsync(async _ => await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var connection = await factory.ConnectAsync(_apiKey, Ct);

        await connection.DisposeAsync();
        var again = async () => await connection.DisposeAsync();

        await again.Should().NotThrowAsync();
    }
}
