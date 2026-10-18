using System.Text.Json;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Kernel.Events;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Collections;

/// <summary>
/// Playlists and box sets ("collections"): hand-made lists of catalog items. Schema <c>collections</c>, its own server with
/// <c>ConnectionStrings:collections</c>. Entries of deleted items are removed when the catalog says so (<see cref="IItemLinkedData"/>).
/// </summary>
public sealed class CollectionsModule : IModule
{
    public const string ModuleName = "collections";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "collections", 5, typeof(CollectionsModule).Assembly,
            "JellyServer.Collections.Migrations."));
        services.AddSingleton<CollectionStore>();
        services.AddItemLinkedData<CollectionStore>();
        services.AddSingleton<CollectionManager>();
        services.AddSingleton<ICollectionManager>(sp => sp.GetRequiredService<CollectionManager>());
        services.AddHostedService<CollectionsUserCleanup>();
        services.AddSingleton<AutoCollections>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoCollections>());
        services.AddSingleton<IScheduledTask, AutoCollectionsTask>();
    }
}

/// <summary>
/// Jellyfin's "Automatically add to collection": movies of a library with that option on, which share a set (TMDb collection, NFO
/// <c>&lt;set&gt;</c>), are put into a box set of that name, created once two movies share it. Runs after scans and after provider
/// metadata was laid over items, since either can bring the set name.
/// </summary>
public sealed class AutoCollections(CollectionManager manager, IMovieSets sets, ILibraryDirectory libraries, IEventBus events, ILogger<AutoCollections> log) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken ct)
    {
        _subscriptions.Add(events.Subscribe<ScanCompleted>((e, c) => RunAsync(e.ChangedItemIds, c)));
        _subscriptions.Add(events.Subscribe<ItemsMetadataApplied>((e, c) => RunAsync(e.ItemIds, c)));
        _subscriptions.Add(events.Subscribe<LibraryOptionsChanged>((e, c) => RunAllAsync(c)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        foreach (var s in _subscriptions) s.Dispose();
        return Task.CompletedTask;
    }

    private bool Enabled(Guid libraryId)
    {
        if (libraries.Find(libraryId) is not { } lib) return false;
        try
        {
            using var doc = JsonDocument.Parse(lib.OptionsJson);
            return doc.RootElement.TryGetProperty("AutomaticallyAddToCollection", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private async Task RunAsync(IReadOnlyList<Guid> changed, CancellationToken ct)
    {
        if (changed.Count == 0 || !libraries.All.Any(l => Enabled(l.Id))) return;
        await _gate.WaitAsync(ct);
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in changed.Chunk(5000))
                foreach (var m in (await sets.OfMoviesAsync(chunk, ct)).Where(m => Enabled(m.LibraryId))) names.Add(m.SetName);
            if (names.Count == 0) return;

            // A set counts every movie of the libraries that have the option on, not only the changed ones.
            var libs = libraries.All.Where(l => Enabled(l.Id)).Select(l => l.Id).ToList();
            await AddAsync(await sets.InSetsAsync(names, libs, ct), ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Every movie of the libraries that have the option on, whether or not anything changed (the option may be new).</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var libs = libraries.All.Where(l => Enabled(l.Id)).Select(l => l.Id).ToList();
            if (libs.Count > 0) await AddAsync(await sets.OfLibrariesAsync(libs, ct), ct);
        }
        finally { _gate.Release(); }
    }

    private async Task AddAsync(IEnumerable<MovieSetRef> movies, CancellationToken ct)
    {
        var groups = movies
            .GroupBy(m => m.SetName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToList(), StringComparer.OrdinalIgnoreCase);
        var created = await manager.AddToNamedBoxSetsAsync(groups, ct);
        if (created > 0) log.LogInformation("{Count} collections created automatically", created);
    }
}

/// <summary>Scheduled pass over the whole library for automatic collections (Jellyfin does it at the end of every scan).</summary>
public sealed class AutoCollectionsTask(AutoCollections auto) : IScheduledTask
{
    public string Id => "3c1f0b7a9e2d4a58b6d4e1a7c9f20b35";
    public string Key => "AutoCollections";
    public string Name => "Create Automatic Collections";
    public string Description => "Groups movies that share a collection into box sets in libraries with automatic collections turned on.";
    public string Category => "Library";

    public IReadOnlyList<TaskTrigger> DefaultTriggers => [TaskTrigger.Interval(TimeSpan.FromDays(7))];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await auto.RunAllAsync(ct);
        progress.Report(100);
    }
}

/// <summary>A deleted user's playlists are handed over, kept or removed the way Jellyfin does it.</summary>
public sealed class CollectionsUserCleanup(CollectionManager manager, IEventBus events, ILogger<CollectionsUserCleanup> log) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = events.Subscribe<UserDeleted>(async (e, c) =>
        {
            await manager.ForgetUserAsync(e.UserId, c);
            log.LogInformation("Playlists of deleted user {UserId} handed over or removed", e.UserId);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
