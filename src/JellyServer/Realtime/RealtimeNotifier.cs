using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using JellyServer.Api;
using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Kernel.Events;
using JellyServer.Serialization;
using JellyServer.UserData.Contracts;

namespace JellyServer.Realtime;

/// <summary>
/// Turns what modules announce on the event bus into the messages jellyfin-web listens for
/// (<c>UserDataChanged</c>, <c>LibraryChanged</c>, <c>ServerShuttingDown</c>), so pages update without a reload.
/// </summary>
public sealed class RealtimeNotifier(
    SocketHub hub, IEventBus events, IUserDataStore userData, ItemRepository items, IHostApplicationLifetime lifetime, ILogger<RealtimeNotifier> log)
    : BackgroundService
{
    // Several changes in a row (marking a season played) go out as one message.
    private static readonly TimeSpan Batch = TimeSpan.FromMilliseconds(200);

    private readonly Channel<UserDataChanged> _userData = Channel.CreateUnbounded<UserDataChanged>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, List<Guid>> _removed = new();
    private readonly List<IDisposable> _subscriptions = [];

    public override Task StartAsync(CancellationToken ct)
    {
        // Handlers only queue: a publisher (a request that marked something played) never waits for sockets or the database.
        _subscriptions.Add(events.Subscribe<UserDataChanged>((e, _) => { _userData.Writer.TryWrite(e); return Task.CompletedTask; }));
        _subscriptions.Add(events.Subscribe<ItemsRemoved>((e, _) =>
        {
            _removed.AddOrUpdate(e.LibraryId, _ => [.. e.ItemIds], (_, list) => { lock (list) list.AddRange(e.ItemIds); return list; });
            return Task.CompletedTask;
        }));
        _subscriptions.Add(events.Subscribe<ScanCompleted>((e, _) => { OnScanCompleted(e); return Task.CompletedTask; }));
        _subscriptions.Add(events.Subscribe<LibraryRemoved>((e, _) => { OnLibraryRemoved(e); return Task.CompletedTask; }));
        lifetime.ApplicationStopping.Register(() => hub.Broadcast("ServerShuttingDown"));
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _userData.Writer.TryComplete();
        return base.StopAsync(ct);
    }

    private void OnScanCompleted(ScanCompleted e)
    {
        var removed = _removed.TryRemove(e.LibraryId, out var gone) ? gone.Distinct().ToList() : [];
        var removedSet = removed.ToHashSet();
        // A scan does not say which of the changed items are new, so all of those still present are reported as added:
        // jellyfin-web only reloads its lists for added/removed items, and a reload for a changed item is merely wasted.
        var info = new LibraryUpdateInfo
        {
            ItemsAdded = [.. e.ChangedItemIds.Where(id => !removedSet.Contains(id)).Select(id => id.N())],
            ItemsRemoved = [.. removed.Select(id => id.N())],
            FoldersAddedTo = [e.LibraryId.N()],
            FoldersRemovedFrom = removed.Count > 0 ? [e.LibraryId.N()] : [],
            CollectionFolders = [e.LibraryId.N()],
        };
        if (!info.IsEmpty) hub.Broadcast("LibraryChanged", JsonSerializer.SerializeToElement(info, AppJson.Default.LibraryUpdateInfo));
    }

    private void OnLibraryRemoved(LibraryRemoved e)
    {
        var info = new LibraryUpdateInfo
        {
            ItemsRemoved = [.. e.ItemIds.Select(id => id.N())],
            FoldersRemovedFrom = [e.LibraryId.N()],
            CollectionFolders = [e.LibraryId.N()],
        };
        hub.Broadcast("LibraryChanged", JsonSerializer.SerializeToElement(info, AppJson.Default.LibraryUpdateInfo));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await _userData.Reader.WaitToReadAsync(ct))
            {
                await Task.Delay(Batch, ct);
                var byUser = new Dictionary<Guid, HashSet<Guid>>();
                while (_userData.Reader.TryRead(out var e))
                {
                    if (!byUser.TryGetValue(e.UserId, out var ids)) byUser[e.UserId] = ids = [];
                    ids.UnionWith(e.ItemIds);
                }
                foreach (var (userId, ids) in byUser) await SendUserDataAsync(userId, ids, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendUserDataAsync(Guid userId, HashSet<Guid> ids, CancellationToken ct)
    {
        try
        {
            // Nobody listening for this user: skip the lookups.
            if (!hub.HasUser(userId)) return;

            var list = ids.ToList();
            var found = await items.GetManyAsync(list);
            var data = await userData.GetManyAsync(userId, list, ct);
            var info = new UserDataChangeInfo
            {
                UserId = userId.N(),
                UserDataList = [.. found.Select(it => DtoBuilder.UserDataDto(it, data.GetValueOrDefault(it.Id)))],
            };
            hub.SendToUser(userId, "UserDataChanged", JsonSerializer.SerializeToElement(info, AppJson.Default.UserDataChangeInfo));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "UserDataChanged for {User} not sent", userId);
        }
    }
}
