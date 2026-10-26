using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Sessions;

namespace InterviewCopilot.Api.Endpoints;

internal static class EventsEndpoints
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        // Pushes session events (answer.delta, answer.done, …) as JSON text frames to overlay and extension clients.
        app.Map("/ws/sessions/{sessionId:guid}/events", async (
                Guid sessionId,
                HttpContext context,
                ClaimsPrincipal user,
                SessionService sessions,
                SessionEventHub hub,
                CancellationToken ct) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    return Results.BadRequest("Expected a WebSocket request.");
                }

                await sessions.GetAsync(user.UserId(), sessionId, ct); // 404 for non-owners, before the upgrade
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var subscription = hub.Subscribe(sessionId);
                await StreamAsync(socket, subscription, ct);
                return Results.Empty;
            })
            .WithTags("Events")
            .RequireAuthorization(Policies.ExtensionScope);
    }

    private static async Task StreamAsync(WebSocket socket, SessionEventSubscription subscription, CancellationToken ct)
    {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiver = WaitForCloseAsync(socket, closed);
        try
        {
            await foreach (var sessionEvent in subscription.Reader.ReadAllAsync(closed.Token))
            {
                var frame = JsonSerializer.SerializeToUtf8Bytes(sessionEvent, WireJson);
                await socket.SendAsync(frame, WebSocketMessageType.Text, true, closed.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The client closed or the server is stopping.
        }
        catch (WebSocketException)
        {
            // The client went away without a close handshake.
        }

        await closed.CancelAsync();
        await receiver;
    }

    /// <summary>Reads (and ignores) client frames until the client closes, then cancels the send loop.</summary>
    private static async Task WaitForCloseAsync(WebSocket socket, CancellationTokenSource closed)
    {
        var buffer = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), closed.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The send loop ended first.
        }
        catch (WebSocketException)
        {
            // The client went away without a close handshake.
        }

        await closed.CancelAsync();
    }
}
