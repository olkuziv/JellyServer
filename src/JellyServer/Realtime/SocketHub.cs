using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using JellyServer.Api;
using JellyServer.Serialization;

namespace JellyServer.Realtime;

/// <summary>One open <c>/socket</c>. Everything is sent through one queue, so nothing ever writes to the socket concurrently.</summary>
public sealed class SocketConnection(WebSocket socket, AuthInfo auth)
{
    // A slow client loses its oldest frames instead of holding memory or stalling whoever broadcasts.
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public AuthInfo Auth { get; } = auth;
    public Guid UserId => Auth.User.Id;
    public bool IsAdmin => Auth.User.IsAdmin;

    public void Post(byte[] frame) => _outbox.Writer.TryWrite(frame);

    public void Close() => _outbox.Writer.TryComplete();

    public async Task PumpAsync(CancellationToken ct)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(frame, WebSocketMessageType.Text, true, ct);
        }
    }
}

/// <summary>A message type a client can subscribe to with <c>{Name}Start</c> / <c>{Name}Stop</c> (Jellyfin's periodic listeners).</summary>
public interface ISocketTopic
{
    string Name { get; }
    void Start(SocketConnection connection, string? data);
    void Stop(SocketConnection connection);
}

/// <summary>The open sockets, and the one place frames are made and sent to them.</summary>
public sealed class SocketHub(IEnumerable<ISocketTopic> topics)
{
    private readonly ConcurrentDictionary<SocketConnection, byte> _connections = new();
    private readonly Dictionary<string, ISocketTopic> _topics = topics.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public int Count => _connections.Count;

    public bool HasUser(Guid userId) => _connections.Keys.Any(c => c.UserId == userId);

    public static byte[] Frame(string type, JsonElement? data = null) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new WebSocketMessage { MessageType = type, MessageId = Guid.NewGuid().ToString("N"), Data = data }, AppJson.Default.WebSocketMessage);

    public void Add(SocketConnection connection) => _connections[connection] = 0;

    public void Remove(SocketConnection connection)
    {
        _connections.TryRemove(connection, out _);
        foreach (var topic in _topics.Values) topic.Stop(connection);
        connection.Close();
    }

    /// <summary>Sends to every open socket (or those <paramref name="where"/> accepts).</summary>
    public void Broadcast(string type, JsonElement? data = null, Func<SocketConnection, bool>? where = null)
    {
        if (_connections.IsEmpty) return;
        var frame = Frame(type, data);
        foreach (var connection in _connections.Keys)
            if (where is null || where(connection)) connection.Post(frame);
    }

    /// <summary>Sends to the sockets of one user.</summary>
    public void SendToUser(Guid userId, string type, JsonElement? data = null) => Broadcast(type, data, c => c.UserId == userId);

    /// <summary>Handles a message from a client; returns the reply, if any.</summary>
    public void OnMessage(SocketConnection connection, string type, string? data)
    {
        if (type == "KeepAlive")
        {
            connection.Post(Frame("KeepAlive"));
            return;
        }
        if (type.EndsWith("Start", StringComparison.Ordinal) && _topics.TryGetValue(type[..^5], out var start)) start.Start(connection, data);
        else if (type.EndsWith("Stop", StringComparison.Ordinal) && _topics.TryGetValue(type[..^4], out var stop)) stop.Stop(connection);
    }
}
