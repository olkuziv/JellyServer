using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Caching;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Catalog;

/// <summary>
/// Jellyfin's "Clean up collections and playlists", for every module that keeps data per item (files, credits, playlist and
/// collection entries, ...): whatever belongs to an item that no longer exists is removed. Deletions are normally passed on at
/// once (<see cref="ItemsRemoved"/>); this catches what a crash or a failed handler left behind.
/// </summary>
public sealed class CleanItemDataTask(IEnumerable<IItemLinkedData> owners, LibraryStore store, RedisCache cache, ILogger<CleanItemDataTask> log)
    : IScheduledTask
{
    private const int PageSize = 5000;

    public string Id => "8f4c2b6e1d3a4f5b9c7e2a1d0b3c4e5f";
    public string Key => "CleanCollectionsAndPlaylists";
    public string Name => "Clean up collections and playlists";
    public string Description => "Removes items from collections and playlists, and the files and people of items that no longer exist.";
    public string Category => "Maintenance";

    // Not at startup: right after an upgrade that empties the catalog the scan is still writing the items back.
    public IReadOnlyList<TaskTrigger> DefaultTriggers => [TaskTrigger.Interval(TimeSpan.FromHours(24))];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        // A scan writes an item's files and credits before its catalog row: while one runs, those would look orphaned.
        await using var scanLock = await cache.TryLockAsync("scan", TimeSpan.FromMinutes(2));
        if (scanLock is null)
        {
            log.LogInformation("Clean-up skipped: a library scan is running");
            return;
        }
        if (await store.CountItemsAsync(ct) == 0)
        {
            // An empty catalog is a catalog being rebuilt (or a database that went missing), never "everything was deleted".
            log.LogInformation("Clean-up skipped: the catalog has no items");
            return;
        }

        var list = owners.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var owner = list[i];
            var removed = 0;
            Guid? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await owner.PageItemIdsAsync(after, PageSize, ct);
                if (page.Count == 0) break;
                after = page[^1];
                var existing = await store.ExistingAsync(page, ct);
                var orphans = page.Where(id => !existing.Contains(id)).ToList();
                if (orphans.Count > 0)
                {
                    await owner.RemoveAsync(orphans, ct);
                    removed += orphans.Count;
                }
                if (page.Count < PageSize) break;
            }
            if (removed > 0) log.LogInformation("Removed {Data} of {Count} items that no longer exist", owner.Name, removed);
            progress.Report(100.0 * (i + 1) / list.Count);
        }
        progress.Report(100);
    }
}
