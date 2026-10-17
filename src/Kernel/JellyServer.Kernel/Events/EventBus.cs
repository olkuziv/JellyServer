using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace JellyServer.Kernel.Events;

/// <summary>
/// In-process events between modules: the publisher owns the event type (in its contracts), subscribers react
/// without the publisher knowing them. Handlers run one after another; one failing never stops the others.
/// Events stay inside this process, so a cluster-wide effect needs a handler that is idempotent per instance.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : notnull;

    IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : notnull;
}

public sealed class EventBus(ILogger<EventBus> log) : IEventBus
{
    private readonly ConcurrentDictionary<Type, Delegate[]> _handlers = new();

    public async Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : notnull
    {
        if (!_handlers.TryGetValue(typeof(T), out var handlers)) return;
        foreach (var handler in handlers)
        {
            try
            {
                await ((Func<T, CancellationToken, Task>)handler)(evt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Handler of {Event} failed", typeof(T).Name);
            }
        }
    }

    public IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : notnull
    {
        _handlers.AddOrUpdate(typeof(T), _ => [handler], (_, existing) => [.. existing, handler]);
        return new Subscription(() => _handlers.AddOrUpdate(typeof(T), _ => [], (_, existing) => existing.Where(h => !ReferenceEquals(h, handler)).ToArray()));
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
