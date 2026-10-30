using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JellyServer.Api;

namespace JellyServer.Realtime;

/// <summary>The Jellyfin client WebSocket: keep-alive, topic subscriptions and server-initiated notifications.</summary>
public static class SocketEndpoint
{
    private const int MaxMessageBytes = 64 * 1024;

    public static void Map(WebApplication app) => app.Map("/socket", HandleAsync);

    private static async Task HandleAsync(HttpContext ctx, AuthService auth, SocketHub hub, ILogger<SocketHub> log)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (await auth.ResolveAsync(ctx) is not { } info)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var ct = ctx.RequestAborted;
        var connection = new SocketConnection(ws, info);
        hub.Add(connection);
        var pump = connection.PumpAsync(ct);
        try
        {
            using (var interval = JsonDocument.Parse("60")) connection.Post(SocketHub.Frame("ForceKeepAlive", interval.RootElement.Clone()));
            await ReceiveAsync(ws, connection, hub, ct);
            if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            log.LogDebug("WebSocket closed for {User}: {Error}", info.User.Name, ex.Message);
        }
        finally
        {
            hub.Remove(connection);
            try { await pump; }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task ReceiveAsync(WebSocket ws, SocketConnection connection, SocketHub hub, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (message.Length + result.Count > MaxMessageBytes)
            {
                await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            Dispatch(text, connection, hub);
        }
    }

    private static void Dispatch(string text, SocketConnection connection, SocketHub hub)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("MessageType", out var type) || type.GetString() is not { } name)
                return;
            string? data = doc.RootElement.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            hub.OnMessage(connection, name, data);
        }
        catch (JsonException)
        {
            // Not a message we understand; Jellyfin ignores those too.
        }
    }
}
