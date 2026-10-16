using System.Net.WebSockets;
using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Audio;

namespace InterviewCopilot.Api.Endpoints;

internal static class AudioEndpoints
{
    public const string AudioPathPrefix = "/ws/";

    public static void MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        // Receives [speaker][PCM16] frames from the desktop capture window (docs/atlas-gotcha/audio-capture-windows.md).
        app.Map("/ws/sessions/{sessionId:guid}/audio", async (
                Guid sessionId,
                HttpContext context,
                ClaimsPrincipal user,
                AudioIngestService ingest,
                CancellationToken ct) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    return Results.BadRequest("Expected a WebSocket request.");
                }

                await ingest.AuthorizeAsync(user.UserId(), sessionId, ct); // 404 / 409 before the upgrade
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await ReceiveAsync(socket, sessionId, ingest, ct);
                return Results.Empty;
            })
            .WithTags("Audio")
            .RequireAuthorization();

        app.MapGet("/api/sessions/{sessionId:guid}/audio/stats", async (
                Guid sessionId,
                ClaimsPrincipal user,
                Application.Sessions.SessionService sessions,
                AudioStatsSink stats,
                CancellationToken ct) =>
            {
                await sessions.GetAsync(user.UserId(), sessionId, ct); // ownership check
                return stats.Get(sessionId);
            })
            .WithTags("Audio")
            .RequireAuthorization();
    }

    private static async Task ReceiveAsync(WebSocket socket, Guid sessionId, AudioIngestService ingest, CancellationToken ct)
    {
        // One byte more than the limit, so an oversized message is detectable.
        var buffer = new byte[AudioFrameParser.MaxMessageBytes + 1];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }

                length += result.Count;
            }
            while (!result.EndOfMessage && length < buffer.Length);

            if (result.MessageType != WebSocketMessageType.Binary
                || !result.EndOfMessage
                || !AudioFrameParser.TryParse(buffer.AsMemory(0, length), out var frame))
            {
                await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Malformed audio frame.", ct);
                return;
            }

            // Copy: the receive buffer is reused for the next message.
            await ingest.IngestAsync(sessionId, frame with { Pcm16 = frame.Pcm16.ToArray() }, ct);
        }
    }
}
