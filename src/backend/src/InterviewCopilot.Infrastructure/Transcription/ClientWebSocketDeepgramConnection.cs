using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>Opens Deepgram live-transcription sockets with <see cref="ClientWebSocket"/>. Only ever connects when asked to.</summary>
internal sealed class ClientWebSocketDeepgramConnectionFactory(IOptions<TranscriptionOptions> options) : IDeepgramConnectionFactory
{
    /// <summary>The <c>wss</c> (or <c>ws</c>) listen URL for the configured base address and model.</summary>
    internal static Uri BuildUri(DeepgramOptions deepgram)
    {
        var builder = new UriBuilder(deepgram.BaseUrl)
        {
            Scheme = string.Equals(deepgram.BaseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
        };
        builder.Path = $"{builder.Path.TrimEnd('/')}/v1/listen";
        builder.Query = "encoding=linear16&sample_rate=16000&channels=1"
            + $"&model={Uri.EscapeDataString(deepgram.Model)}&interim_results=false&punctuate=true";
        return builder.Uri;
    }

    public async ValueTask<IDeepgramConnection> ConnectAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var deepgram = options.Value.Providers.Deepgram;
        if (!deepgram.Enabled)
        {
            throw new DeepgramConnectionException("Deepgram is disabled in configuration.");
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Token {apiKey}");
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(BuildUri(deepgram), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();

            // The inner exception is dropped on purpose: nothing about the request (the key) may travel with it.
            throw status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new DeepgramRejectedKeyException()
                : new DeepgramConnectionException("Couldn't open the Deepgram connection.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new ClientWebSocketDeepgramConnection(socket);
    }
}

/// <summary>One open Deepgram socket. Sends come from a single caller and receives from another, as the socket allows.</summary>
internal sealed class ClientWebSocketDeepgramConnection(WebSocket socket) : IDeepgramConnection
{
    private static readonly byte[] KeepAliveMessage = Encoding.UTF8.GetBytes("""{"type":"KeepAlive"}""");
    private static readonly byte[] CloseStreamMessage = Encoding.UTF8.GetBytes("""{"type":"CloseStream"}""");

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken) =>
        SendAsync(pcm16, WebSocketMessageType.Binary, cancellationToken);

    public ValueTask SendKeepAliveAsync(CancellationToken cancellationToken) =>
        SendAsync(KeepAliveMessage, WebSocketMessageType.Text, cancellationToken);

    public ValueTask SendCloseStreamAsync(CancellationToken cancellationToken) =>
        SendAsync(CloseStreamMessage, WebSocketMessageType.Text, cancellationToken);

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return socket.CloseStatus is null or WebSocketCloseStatus.NormalClosure
                        ? null
                        : throw new DeepgramConnectionException("Deepgram closed the connection with an error.");
                }

                buffer.Advance(result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new DeepgramConnectionException("The Deepgram connection dropped.");
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Already gone; nothing left to close.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(payload, type, endOfMessage: true, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new DeepgramConnectionException("The Deepgram connection dropped.");
        }
    }
}
