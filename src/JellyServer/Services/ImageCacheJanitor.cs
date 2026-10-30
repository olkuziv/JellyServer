using JellyServer.Configuration;
using Microsoft.Extensions.Options;

namespace JellyServer.Services;

/// <summary>
/// Keeps the image cache under <see cref="ImageOptions.CacheMaxMB"/> by deleting the least recently used files.
/// Everything in the cache can be rebuilt (variants are resized again, remote artwork is fetched again),
/// so eviction never loses data. Uploaded images live elsewhere and are not touched.
/// </summary>
public sealed class ImageCacheJanitor(IOptions<ServerOptions> options, ImageService images, ILogger<ImageCacheJanitor> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StaleTemp = TimeSpan.FromHours(1);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var max = (long)options.Value.Images.CacheMaxMB * 1024 * 1024;
        if (max <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var freed = await Task.Run(() => Evict(images.CacheDir, max), stoppingToken);
                if (freed > 0) log.LogInformation("Image cache over {Max} MB: evicted {Freed} MB", options.Value.Images.CacheMaxMB, freed / (1024 * 1024));
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Deletes stale temp files, then the oldest files until the cache is down to 90% of <paramref name="maxBytes"/>
    /// (a margin, so it does not evict on every pass). Returns the bytes freed.
    /// </summary>
    public static long Evict(string dir, long maxBytes)
    {
        if (maxBytes <= 0 || !Directory.Exists(dir)) return 0;
        long freed = 0, total = 0;
        var files = new List<FileInfo>();
        var staleBefore = DateTime.UtcNow - StaleTemp;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (f.LastWriteTimeUtc < staleBefore) freed += TryDelete(f);
                continue; // being written right now, or just removed
            }
            files.Add(f);
            total += f.Length;
        }
        if (total <= maxBytes) return freed;

        var goal = maxBytes / 10 * 9;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= goal) break;
            var len = TryDelete(f);
            total -= len;
            freed += len;
        }
        return freed;
    }

    /// <summary>
    /// Deletes cache files not used for <paramref name="age"/> (everything for a zero age) and the folders left empty.
    /// A file counts as used when it was written or, for hits, touched (see <see cref="Touch"/>). Fresh temp files are being written
    /// right now and stay. Returns the bytes freed; <paramref name="progress"/> gets 0-100.
    /// </summary>
    public static long ClearOlderThan(string dir, TimeSpan age, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return 0;
        var now = DateTime.UtcNow;
        var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        long freed = 0;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            var temp = f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            var limit = temp ? StaleTemp : age;
            if (now - f.LastWriteTimeUtc >= limit) freed += TryDelete(f);
            if (i % 200 == 0) progress?.Report(100.0 * i / files.Count);
        }
        foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length).ToList())
        {
            try { if (!sub.EnumerateFileSystemInfos().Any()) sub.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next time */ }
        }
        progress?.Report(100);
        return freed;
    }

    private static long TryDelete(FileInfo f)
    {
        try
        {
            var len = f.Length;
            f.Delete();
            return len;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0; // in use: the next pass gets it
        }
    }

    /// <summary>Marks a cache file as used. Throttled to once a day so serving a hit does not write to the disk.</summary>
    public static void Touch(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TouchEvery) File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
