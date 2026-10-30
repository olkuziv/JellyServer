using System.Collections.Concurrent;
using System.Text.Json;

namespace JellyServer.Realtime;

/// <summary>
/// A subscribable topic that sends a snapshot: right after <c>{Name}Start</c> (<c>"dueMs,intervalMs"</c>), then every interval,
/// and at once when <see cref="Notify"/> is called. A snapshot identical to the last one sent to that client is not sent again.
/// </summary>
public abstract class PeriodicTopic : ISocketTopic
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    private sealed class Subscription(SocketConnection connection, CancellationTokenSource cts)
    {
        public SocketConnection Connection { get; } = connection;
        public CancellationTokenSource Cts { get; } = cts;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Last;
    }

    private readonly ConcurrentDictionary<SocketConnection, Subscription> _subs = new();

    public abstract string Name { get; }

    /// <summary>The snapshot for one client; null sends nothing.</summary>
    protected abstract Task<JsonElement?> BuildAsync(SocketConnection connection, CancellationToken ct);

    public void Start(SocketConnection connection, string? data)
    {
        Stop(connection);
        var (due, interval) = Parse(data);
        var sub = new Subscription(connection, new CancellationTokenSource());
        _subs[connection] = sub;
        _ = Task.Run(() => LoopAsync(sub, due, interval));
    }

    public void Stop(SocketConnection connection)
    {
        if (!_subs.TryRemove(connection, out var sub)) return;
        sub.Cts.Cancel();
    }

    /// <summary>Something changed: every subscriber gets a fresh snapshot now (if it differs from its last).</summary>
    public void Notify()
    {
        foreach (var sub in _subs.Values) _ = PushAsync(sub);
    }

    private static (TimeSpan Due, TimeSpan Interval) Parse(string? data)
    {
        long due = 0, interval = (long)DefaultInterval.TotalMilliseconds;
        if (!string.IsNullOrWhiteSpace(data))
        {
            var parts = data.Split(',');
            if (parts.Length > 0) long.TryParse(parts[0], out due);
            if (parts.Length > 1) long.TryParse(parts[1], out interval);
        }
        return (TimeSpan.FromMilliseconds(Math.Clamp(due, 0, 60_000)), TimeSpan.FromMilliseconds(Math.Max(interval, MinInterval.TotalMilliseconds)));
    }

    private async Task LoopAsync(Subscription sub, TimeSpan due, TimeSpan interval)
    {
        var ct = sub.Cts.Token;
        try
        {
            if (due > TimeSpan.Zero) await Task.Delay(due, ct);
            using var timer = new PeriodicTimer(interval);
            do { await PushAsync(sub); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task PushAsync(Subscription sub)
    {
        var ct = sub.Cts.Token;
        try
        {
            await sub.Gate.WaitAsync(ct);
            try
            {
                if (await BuildAsync(sub.Connection, ct) is not { } data) return;
                var text = data.GetRawText();
                if (text == sub.Last) return;
                sub.Last = text;
                sub.Connection.Post(SocketHub.Frame(Name, data));
            }
            finally
            {
                sub.Gate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
