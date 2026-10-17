using System.Threading.Channels;
using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Events;
using JellyServer.Metadata.Contracts;
using JellyServer.Settings.Contracts;
using JellyServer.Tasks.Contracts;
using Microsoft.Extensions.Options;

namespace JellyServer.Metadata;

/// <summary>
/// The metadata work queue. Work comes from finished scans, a periodic walk over the catalog and manual refreshes; workers ask the
/// providers each library has enabled (in the library's order), combine the answers (the first provider wins, the next ones fill gaps)
/// and give the result to the catalog. A movie is one lookup; a series is looked up first, then its seasons (one request per season
/// returns the episodes too).
/// </summary>
public sealed class MetadataEngine(
    IItemRefs items, ILibraryDirectory libraries, IEnumerable<IMetadataProvider> providers, IRemoteMetadataSink sink, IMetadataStore store,
    IServerConfiguration config, IEventBus events, IOptions<MetadataOptions> options, IServiceProvider services, ILogger<MetadataEngine> log) : BackgroundService, IMetadataRefresh
{
    private static readonly HashSet<string> Supported = [ItemTypes.Movie, ItemTypes.Series, ItemTypes.Season, ItemTypes.Episode];
    private const int MovieBatch = 20;
    private const int SinkBatch = 100;

    /// <summary>A series with the seasons and episodes to look up, or a batch of movies of one library.</summary>
    private sealed record Unit(Guid LibraryId, ItemRef? Series, bool SeriesNeedsWork, IReadOnlyList<ItemRef> Items, bool ReplaceMetadata, bool ReplaceImages);

    private readonly Channel<Unit> _queue = Channel.CreateUnbounded<Unit>();
    private readonly HashSet<Guid> _pending = [];
    private int _batchTotal;
    private readonly MetadataOptions _opt = options.Value;
    private IDisposable? _scans, _removed;

    public override Task StartAsync(CancellationToken ct)
    {
        _scans = events.Subscribe<ScanCompleted>((e, c) => EnqueueChangedAsync(e.ChangedItemIds, c));
        _removed = events.Subscribe<LibraryRemoved>((e, c) => store.DeleteAsync(e.ItemIds, c));
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        _scans?.Dispose();
        _removed?.Dispose();
        return base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var workers = Enumerable.Range(0, Math.Max(1, _opt.Concurrency)).Select(_ => Task.Run(() => WorkerAsync(ct), ct)).ToList();
        await Task.WhenAll(workers);
    }

    // ---- sources of work ----

    /// <summary>Walks the whole catalog and queues what nobody has looked up yet (or what is due for another try).</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!providers.Any(p => p.IsAvailable)) return;
        Guid? after = null;
        var queued = 0;
        while (true)
        {
            var page = await items.PageAsync(after, 2000, ct);
            if (page.Count == 0) break;
            after = page[^1].Id;
            queued += await EnqueueDueAsync(page, ct);
        }
        if (queued > 0) log.LogInformation("Metadata: {Count} items queued", queued);
    }

    public async Task QueueAllAsync(CancellationToken ct = default)
    {
        if (!providers.Any(p => p.IsAvailable)) return;
        Guid? after = null;
        while (true)
        {
            var page = await items.PageAsync(after, 2000, ct);
            if (page.Count == 0) break;
            after = page[^1].Id;
            await EnqueueAsync(page.Where(r => Supported.Contains(r.Type)).ToList(), replaceMetadata: false, replaceImages: false, ct);
        }
    }

    private async Task EnqueueChangedAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (!providers.Any(p => p.IsAvailable)) return;
        var queued = 0;
        foreach (var chunk in ids.Chunk(2000))
            queued += await EnqueueDueAsync(await items.GetManyAsync(chunk, ct), ct);
        if (queued > 0) ShowAsTask();
    }

    /// <summary>The queue is worked off in the background; the "Download Metadata" task mirrors it so the dashboard shows the progress.</summary>
    private void ShowAsTask() => services.GetService<ITaskManager>()?.Start(DownloadMetadataTask.TaskId);

    private async Task<int> EnqueueDueAsync(IReadOnlyList<ItemRef> refs, CancellationToken ct)
    {
        var candidates = refs.Where(r => Supported.Contains(r.Type)).ToList();
        var due = await store.DueAsync(candidates.Select(r => r.Id).ToList(), _opt.NotFoundRetry, _opt.FailedRetry, ct);
        var work = candidates.Where(r => due.Contains(r.Id)).ToList();
        await EnqueueAsync(work, replaceMetadata: false, replaceImages: false, ct);
        return work.Count;
    }

    public async Task RefreshAsync(MetadataRefreshRequest request, CancellationToken ct = default)
    {
        var self = await items.GetManyAsync([request.ItemId], ct);
        var all = new List<ItemRef>(self);
        if (request.Recursive) all.AddRange(await items.GetDescendantsAsync(request.ItemId, ct));
        await EnqueueAsync(all.Where(r => Supported.Contains(r.Type)).ToList(), request.ReplaceMetadata, request.ReplaceImages, ct);
        ShowAsTask();
    }

    private async Task EnqueueAsync(List<ItemRef> work, bool replaceMetadata, bool replaceImages, CancellationToken ct)
    {
        List<ItemRef> fresh;
        lock (_pending)
        {
            fresh = work.Where(r => _pending.Add(r.Id)).ToList();
            _batchTotal += fresh.Count;
        }
        if (fresh.Count == 0) return;

        foreach (var group in fresh.Where(r => r.Type == ItemTypes.Movie).GroupBy(r => r.LibraryId))
            foreach (var chunk in group.Chunk(MovieBatch))
                await _queue.Writer.WriteAsync(new Unit(group.Key, null, false, chunk, replaceMetadata, replaceImages), ct);

        foreach (var series in fresh.Where(r => r.Type != ItemTypes.Movie).GroupBy(r => r.Type == ItemTypes.Series ? r.Id : r.SeriesId ?? r.Id))
        {
            var seriesRef = series.FirstOrDefault(r => r.Type == ItemTypes.Series) ?? (await items.GetManyAsync([series.Key], ct)).FirstOrDefault();
            if (seriesRef is null)
            {
                Release(series.Select(r => r.Id));
                continue;
            }
            var needsWork = series.Any(r => r.Type == ItemTypes.Series);
            await _queue.Writer.WriteAsync(new Unit(seriesRef.LibraryId, seriesRef, needsWork, series.Where(r => r.Type != ItemTypes.Series).ToList(), replaceMetadata, replaceImages), ct);
        }
    }

    private void Release(IEnumerable<Guid> ids)
    {
        lock (_pending)
        {
            foreach (var id in ids) _pending.Remove(id);
            if (_pending.Count == 0) _batchTotal = 0;
        }
    }

    public void Cancel()
    {
        // Units still in the channel are read and released here; the workers keep what they are doing.
        while (_queue.Reader.TryRead(out var unit))
            Release(unit.Items.Select(i => i.Id).Concat(unit.SeriesNeedsWork && unit.Series is { } s ? [s.Id] : []));
    }

    public MetadataProgress Progress
    {
        get { lock (_pending) return new MetadataProgress(_pending.Count, _batchTotal); }
    }
    // ---- workers ----

    private async Task WorkerAsync(CancellationToken ct)
    {
        await foreach (var unit in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessAsync(unit, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Metadata lookup failed for {Count} items", unit.Items.Count);
            }
            finally
            {
                Release(unit.Items.Select(i => i.Id).Concat(unit.SeriesNeedsWork && unit.Series is { } s ? [s.Id] : []));
            }
        }
    }

    private async Task ProcessAsync(Unit unit, CancellationToken ct)
    {
        if (libraries.Find(unit.LibraryId) is not { } library) return;
        var doc = await config.GetAsync(ct);
        string serverLanguage = doc["PreferredMetadataLanguage"]?.GetValue<string>() ?? "en", serverCountry = doc["MetadataCountryCode"]?.GetValue<string>() ?? "US";
        MetadataPlan Plan(string type) => MetadataPlanner.Create(library, type, providers, serverLanguage, serverCountry);

        var updates = new List<RemoteMetadataUpdate>();
        var states = new List<(Guid, string)>();

        if (unit.Series is null)
        {
            foreach (var movie in unit.Items)
                states.Add((movie.Id, (await LookUpAsync(movie, movie.ProviderIds, Plan(movie.Type), unit, updates, ct)).Status));
        }
        else
        {
            await ProcessSeriesAsync(unit, Plan, updates, states, ct);
        }

        for (var i = 0; i < updates.Count; i += SinkBatch)
        {
            var batch = updates.GetRange(i, Math.Min(SinkBatch, updates.Count - i));
            // What was stored before tells the catalog which values came from a provider; the new answers are stored first, so a crash
            // between the two steps leaves them for the next scan to lay over the item.
            var previous = await store.LoadAsync(batch.Select(u => u.ItemId).Distinct().ToList(), ct);
            await store.SaveRemoteAsync(batch.Select(u => (u.ItemId, u.Metadata)).ToList(), ct);
            await sink.ApplyAsync(batch.Select(u => u with { Previous = previous.GetValueOrDefault(u.ItemId) }).ToList(), ct);
        }
        await store.SaveAsync(states, ct);
    }

    /// <summary>Asks the plan's providers about one item; returns the combined metadata (null when nobody knew it) and how it went.</summary>
    private async Task<(RemoteMetadata? Metadata, string Status)> LookUpAsync(ItemRef item, IReadOnlyDictionary<string, string> knownIds, MetadataPlan plan, Unit unit, List<RemoteMetadataUpdate> updates, CancellationToken ct)
    {
        if (plan.Providers.Count == 0) return (null, ItemStatus.Skipped);
        var ids = new Dictionary<string, string>(knownIds);
        RemoteMetadata? merged = null;
        var failed = false;
        foreach (var pick in plan.Providers)
        {
            ProviderResult result;
            try { result = await pick.Provider.FetchAsync(new MetadataRequest(item, ids, plan.Context), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "{Provider} failed for {Item}", pick.Provider.Name, item.Name);
                result = ProviderResult.Failed;
            }
            if (result.Status == ProviderStatus.Failed) failed = true;
            if (result.Status != ProviderStatus.Found || result.Metadata is null) continue;
            var md = Restrict(result.Metadata, pick, plan);
            merged = merged is null ? md : merged.MergeUnder(md);
            foreach (var (key, value) in md.ProviderIds) ids.TryAdd(key, value);
        }
        if (merged is null) return (null, failed ? ItemStatus.Failed : ItemStatus.NotFound);
        updates.Add(new RemoteMetadataUpdate(item.Id, merged, unit.ReplaceMetadata, unit.ReplaceImages));
        return (merged, ItemStatus.Found);
    }

    private async Task ProcessSeriesAsync(Unit unit, Func<string, MetadataPlan> plan, List<RemoteMetadataUpdate> updates, List<(Guid, string)> states, CancellationToken ct)
    {
        var series = unit.Series!;
        var ids = new Dictionary<string, string>(series.ProviderIds);
        var seriesFound = true;
        if (unit.SeriesNeedsWork)
        {
            var (md, status) = await LookUpAsync(series, ids, plan(ItemTypes.Series), unit, updates, ct);
            states.Add((series.Id, status));
            if (md is not null) foreach (var (key, value) in md.ProviderIds) ids.TryAdd(key, value);
            seriesFound = md is not null;
        }
        else
        {
            seriesFound = ids.Count > 0;
        }

        if (!seriesFound)
        {
            foreach (var item in unit.Items) states.Add((item.Id, ItemStatus.NotFound));
            return;
        }

        var seasonPlan = plan(ItemTypes.Season);
        var episodePlan = plan(ItemTypes.Episode);
        var numbers = unit.Items.Select(i => i.Type == ItemTypes.Season ? i.IndexNumber : i.ParentIndexNumber).OfType<int>().Distinct().Order().ToList();
        foreach (var number in numbers)
        {
            var seasonItems = unit.Items.Where(i => i.Type == ItemTypes.Season && i.IndexNumber == number).ToList();
            var episodeItems = unit.Items.Where(i => i.Type == ItemTypes.Episode && i.ParentIndexNumber == number && i.IndexNumber is not null).ToList();
            RemoteMetadata? season = null;
            var episodes = new Dictionary<int, RemoteMetadata>();
            var failed = false;

            foreach (var provider in episodePlan.Providers.Select(p => p.Provider).Union(seasonPlan.Providers.Select(p => p.Provider)))
            {
                var seasonPick = seasonPlan.Providers.FirstOrDefault(p => p.Provider == provider);
                var episodePick = episodePlan.Providers.FirstOrDefault(p => p.Provider == provider);
                SeasonProviderResult result;
                try { result = await provider.FetchSeasonAsync(new SeasonRequest(series, ids, number, episodePlan.Context), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "{Provider} failed for {Series} season {Season}", provider.Name, series.Name, number);
                    result = SeasonProviderResult.Failed;
                }
                if (result.Status == ProviderStatus.Failed) failed = true;
                if (result.Status != ProviderStatus.Found) continue;
                if (result.Season is not null && seasonPick is not null)
                {
                    var md = Restrict(result.Season, seasonPick, seasonPlan);
                    season = season is null ? md : season.MergeUnder(md);
                }
                if (episodePick is null) continue;
                foreach (var (episode, data) in result.Episodes)
                {
                    var md = Restrict(data, episodePick, episodePlan);
                    episodes[episode] = episodes.TryGetValue(episode, out var have) ? have.MergeUnder(md) : md;
                }
            }

            foreach (var item in seasonItems)
            {
                if (season is not null) updates.Add(new RemoteMetadataUpdate(item.Id, season, unit.ReplaceMetadata, unit.ReplaceImages));
                states.Add((item.Id, season is not null ? ItemStatus.Found : failed ? ItemStatus.Failed : seasonPlan.Providers.Count == 0 ? ItemStatus.Skipped : ItemStatus.NotFound));
            }
            foreach (var item in episodeItems)
            {
                if (episodes.TryGetValue(item.IndexNumber!.Value, out var md))
                {
                    updates.Add(new RemoteMetadataUpdate(item.Id, md, unit.ReplaceMetadata, unit.ReplaceImages));
                    states.Add((item.Id, ItemStatus.Found));
                }
                else states.Add((item.Id, failed ? ItemStatus.Failed : episodePlan.Providers.Count == 0 ? ItemStatus.Skipped : ItemStatus.NotFound));
            }
        }
        // Seasons and episodes without a usable number cannot be matched.
        var handled = states.Select(s => s.Item1).ToHashSet();
        foreach (var item in unit.Items.Where(i => !handled.Contains(i.Id))) states.Add((item.Id, ItemStatus.NotFound));
    }

    /// <summary>Keeps only what the library allows from this provider: fields and/or artwork, within the backdrop limit.</summary>
    public static RemoteMetadata Restrict(RemoteMetadata md, ProviderPick pick, MetadataPlan plan)
    {
        if (!pick.UseMetadata)
            md = new RemoteMetadata { Images = md.Images, ProviderIds = md.ProviderIds };
        if (!pick.UseImages)
            md.Images = new ItemImages();
        if (md.Images.Backdrops.Count > plan.BackdropLimit)
            md.Images.Backdrops = md.Images.Backdrops.Take(plan.BackdropLimit).ToList();
        if (plan.ImageLimits.GetValueOrDefault("Primary", 1) == 0) md.Images.Primary = null;
        if (plan.ImageLimits.GetValueOrDefault("Logo", 1) == 0) md.Images.Logo = null;
        if (plan.ImageLimits.GetValueOrDefault("Thumb", 1) == 0) md.Images.Thumb = null;
        if (plan.ImageLimits.GetValueOrDefault("Banner", 1) == 0) md.Images.Banner = null;
        return md;
    }
}