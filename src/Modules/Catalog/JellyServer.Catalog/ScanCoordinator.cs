using JellyServer.Kernel.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using JellyServer.Kernel;
using JellyServer.Kernel.Caching;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>
/// Incremental, parallel library scanner.
///
/// Each item stores a fingerprint of every file that contributed to it (video/.strm,
/// nfo, artwork, subtitles: name+size+mtime). Unchanged items are skipped without
/// opening a single file, so rescanning 100k items costs one readdir+stat per file.
/// Changes are written in batches through binary COPY.
/// </summary>
public sealed class ScanCoordinator(
    LibraryStore store,
    RedisCache cache,
    IOptions<ScannerOptions> options,
    IEnumerable<ILibraryScanner> scanners,
    IEventBus events,
    ILogger<ScanCoordinator> log)
{
    private readonly ScannerOptions _opt = options.Value;
    private readonly Dictionary<string, ILibraryScanner> _scanners = scanners
        .SelectMany(s => s.CollectionTypes.Select(t => (Type: t, Scanner: s))).ToDictionary(x => x.Type, x => x.Scanner, StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _log = log;
    private int _running;
    private volatile ScanStatus _status = new();

    public ScanStatus Status => _status;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Scans every library; only one scan runs per cluster (Redis lock).</summary>
    public async Task<bool> ScanAllAsync(CancellationToken ct, IProgress<double>? progress = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        try
        {
            await using var scanLock = await cache.TryLockAsync("scan", TimeSpan.FromMinutes(2));
            if (scanLock is null)
            {
                log.LogInformation("Scan skipped: another instance holds the scan lock");
                return false;
            }
            var libraries = await store.LoadLibrariesAsync(ct);
            var started = DateTime.UtcNow;
            _status = new ScanStatus { Running = true, StartedAt = started };
            log.LogInformation("Scan started for {Count} libraries", libraries.Count);

            var results = new List<LibraryScanResult>();
            string? lastError = null;
            for (var i = 0; i < libraries.Count; i++)
            {
                var lib = libraries[i];
                if (ct.IsCancellationRequested) break;
                progress?.Report(100.0 * i / libraries.Count);
                _status = _status with { Library = lib.Name };
                try
                {
                    results.Add(await ScanLibraryAsync(lib, ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = $"{lib.Name}: {ex.Message}";
                    log.LogError(ex, "Library scan failed for {Library}", lib.Name);
                }
            }
            _status = new ScanStatus
            {
                Running = false, StartedAt = started, FinishedAt = DateTime.UtcNow,
                Libraries = results, LastError = lastError, Cancelled = ct.IsCancellationRequested,
            };
            log.LogInformation("Scan finished in {Ms} ms", (long)(DateTime.UtcNow - started).TotalMilliseconds);
            return true;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<LibraryScanResult> ScanLibraryAsync(Library lib, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var scope = _log.BeginScope("Library {Library}", lib.Name);
        var existing = await store.LoadFingerprintsAsync(lib.Id, ct);
        if (!_scanners.TryGetValue(lib.CollectionType, out var scanner))
            throw new InvalidOperationException($"No scanner is registered for library type '{lib.CollectionType}'");
        var channel = Channel.CreateBounded<Item>(_opt.BatchSize * 4);
        var scan = new ScanContext(lib, existing, channel.Writer, _opt.Workers, _log);

        // Single writer: batches items into COPY + merge transactions.
        var changed = new ConcurrentBag<Guid>();
        long upserted = 0;
        Exception? writeError = null;
        var writer = Task.Run(async () =>
        {
            var batch = new List<Item>(_opt.BatchSize);
            async Task Flush()
            {
                if (batch.Count == 0) return;
                try
                {
                    // A rewritten item lost what providers added: lay it over again (one extra query per batch).
                    var remote = await store.LoadRemoteAsync(batch.Select(b => b.Id).ToList(), ct);
                    foreach (var it in batch)
                        if (remote.TryGetValue(it.Id, out var r)) RemoteMetadataOverlay.Apply(it, null, r, replaceAll: false);
                    await store.UpsertAsync(batch, ct);
                    Interlocked.Add(ref upserted, batch.Count);
                    foreach (var it in batch) changed.Add(it.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    writeError = ex;
                    log.LogError(ex, "Upsert of {Count} items failed", batch.Count);
                }
                batch.Clear();
            }
            await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                batch.Add(item);
                if (batch.Count >= _opt.BatchSize) await Flush();
            }
            await Flush();
        }, CancellationToken.None);

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(progressCts.Token))
                    log.LogInformation("Scan progress {Library}: dirs={Dirs} files={Files} unchanged={Unchanged} upserted={Upserted}",
                        lib.Name, scan.Dirs, scan.FilesSeen, scan.Unchanged, Interlocked.Read(ref upserted));
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        try
        {
            foreach (var root in lib.Paths)
            {
                ct.ThrowIfCancellationRequested();
                await scanner.ScanRootAsync(scan, root, ct);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            await writer;
            await progressCts.CancelAsync();
            await progress;
        }
        ct.ThrowIfCancellationRequested();

        // Remove items whose files disappeared - but never when a root was unreadable.
        var deleted = 0;
        if (!scan.RootError && writeError is null)
        {
            var gone = existing.Keys.Where(id => !scan.Seen.ContainsKey(id)).ToList();
            if (gone.Count > 0)
            {
                deleted = await store.DeleteAsync(gone, ct);
                foreach (var id in gone) changed.Add(id);
            }
        }
        foreach (var id in await store.RefreshAggregatesAsync(lib.Id, ct)) changed.Add(id);

        if (!changed.IsEmpty)
        {
            await cache.DeleteAsync(changed.Distinct().Select(id => "item:" + id.N()));
            await cache.BumpGenerationAsync();
            await events.PublishAsync(new ScanCompleted(lib.Id, changed.Distinct().ToList()), ct);
        }

        var result = new LibraryScanResult(lib.Name, scan.Dirs, scan.FilesSeen, scan.Unchanged,
            Interlocked.Read(ref upserted), deleted, sw.ElapsedMilliseconds,
            scan.RootError ? "a library root was missing or empty; deletions skipped" : null);
        log.LogInformation(
            "Library {Library} scanned: dirs={Dirs} files={Files} unchanged={Unchanged} upserted={Upserted} deleted={Deleted} in {Ms} ms",
            lib.Name, result.Dirs, result.Files, result.Unchanged, result.Upserted, result.Deleted, result.DurationMs);
        if (writeError is not null) throw new InvalidOperationException("Some batches failed to write", writeError);
        return result;
    }
}

public sealed record ScanStatus
{
    public bool Running { get; init; }
    public string? Library { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? LastError { get; init; }
    public bool Cancelled { get; init; }
    public List<LibraryScanResult> Libraries { get; init; } = [];
}

public sealed record LibraryScanResult(
    string Name, long Dirs, long Files, long Unchanged, long Upserted, long Deleted, long DurationMs, string? Note);
