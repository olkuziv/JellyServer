using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using JellyServer.Configuration;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace JellyServer.Services;

/// <summary>
/// Serves artwork: local files or remote NFO URLs (downloaded once), optionally
/// resized. Every variant is cached on disk, keyed by source + size, so each
/// poster is decoded at most once per size bucket. Concurrent identical requests
/// share one resize, and total resize concurrency is bounded by CPU count.
/// </summary>
public sealed class ImageService
{
    private readonly ImageOptions _opt;
    private readonly string _cacheDir;
    private readonly string _uploadDir;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ImageService> _log;
    private readonly SemaphoreSlim _cpu = new(Math.Max(2, Environment.ProcessorCount));
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    public ImageService(IOptions<ServerOptions> options, IHttpClientFactory http, ILogger<ImageService> log)
    {
        _opt = options.Value.Images;
        _cacheDir = _opt.CacheDir ?? Path.Combine(options.Value.DataDir, "cache", "images");
        Directory.CreateDirectory(_cacheDir);
        _uploadDir = Path.GetFullPath(Path.Combine(options.Value.DataDir, "images", "uploads")) + Path.DirectorySeparatorChar;
        _http = http;
        _log = log;
    }

    public sealed record ImageRequest(int? MaxWidth, int? MaxHeight, int? FillWidth, int? FillHeight, int? Quality);

    /// <summary>Returns a local file path for the requested variant, or null when unavailable.</summary>
    public async Task<string?> GetAsync(string source, ImageRequest req, CancellationToken ct)
    {
        var original = await ResolveSourceAsync(source, ct);
        if (original is null || !_opt.Resize) return original;

        var (w, h) = (req.FillWidth ?? req.MaxWidth, req.FillHeight ?? req.MaxHeight);
        if (w is null && h is null) return original;
        // Round to 50px buckets: far fewer variants on disk, visually identical.
        w = w is { } ww ? Bucket(ww) : null;
        h = h is { } hh ? Bucket(hh) : null;
        var quality = Math.Clamp(req.Quality ?? _opt.JpegQuality, 30, 100);
        var info = new FileInfo(original);
        var key = Hash($"{original}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{w}|{h}|{quality}");
        var ext = original.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var target = Path.Combine(_cacheDir, key[..2], key + ext);
        if (File.Exists(target)) return target;

        var lazy = _inflight.GetOrAdd(target, t => new Lazy<Task<string?>>(() => ResizeAsync(original, t, w, h, quality)));
        try { return await lazy.Value; }
        finally { _inflight.TryRemove(target, out _); }
    }

    /// <summary>Extension for an uploaded image's content type, or null when it is not an image we serve.</summary>
    public static string? UploadExtension(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => null,
    };

    /// <summary>
    /// Stores an uploaded image under the data dir. Each upload gets its own file name, so the image tag
    /// (derived from the path) changes and clients drop the old picture.
    /// </summary>
    public async Task<string> SaveUploadAsync(Guid ownerId, string type, string extension, byte[] data, CancellationToken ct)
    {
        var dir = Path.Combine(_uploadDir, ownerId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{type.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}");
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Removes a file this service stored; paths elsewhere (library folders, remote URLs) are left alone.</summary>
    public void DeleteUpload(string? path)
    {
        if (path is null || !Path.GetFullPath(path).StartsWith(_uploadDir, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); }
        catch (IOException ex) { _log.LogDebug("Could not delete {Path}: {Error}", path, ex.Message); }
    }

    private static int Bucket(int v) => Math.Clamp((v + 49) / 50 * 50, 50, 3840);

    private async Task<string?> ResizeAsync(string source, string target, int? maxW, int? maxH, int quality)
    {
        await _cpu.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var codec = SKCodec.Create(source);
                if (codec is null) return source; // undecodable: serve as-is
                var (sw, sh) = (codec.Info.Width, codec.Info.Height);
                var scale = Math.Min(maxW is { } mw ? (double)mw / sw : double.MaxValue, maxH is { } mh ? (double)mh / sh : double.MaxValue);
                if (scale >= 1) return source; // never upscale
                var (tw, th) = (Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale)));

                using var bitmap = SKBitmap.Decode(codec);
                if (bitmap is null) return source;
                using var resized = bitmap.Resize(new SKImageInfo(tw, th), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is null) return source;
                using var image = SKImage.FromBitmap(resized);
                var format = target.EndsWith(".png") ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                using var data = image.Encode(format, quality);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var tmp = target + "." + Environment.CurrentManagedThreadId + ".tmp";
                using (var fs = File.Create(tmp)) data.SaveTo(fs);
                File.Move(tmp, target, overwrite: true); // atomic publish
                return target;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Resize failed for {Source}: {Error}", source, ex.Message);
            return source;
        }
        finally
        {
            _cpu.Release();
        }
    }

    /// <summary>Local paths are used directly; remote URLs are downloaded once into the cache.</summary>
    private async Task<string?> ResolveSourceAsync(string source, CancellationToken ct)
    {
        if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return File.Exists(source) ? source : null;
        if (!_opt.RemoteFetch) return null;

        var key = Hash(source);
        var ext = Path.GetExtension(new Uri(source).AbsolutePath) is { Length: > 1 and <= 5 } e ? e.ToLowerInvariant() : ".jpg";
        var path = Path.Combine(_cacheDir, "remote", key[..2], key + ext);
        if (File.Exists(path)) return path;

        var lazy = _inflight.GetOrAdd(path, p => new Lazy<Task<string?>>(() => DownloadAsync(source, p)));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { _inflight.TryRemove(path, out _); }
    }

    private async Task<string?> DownloadAsync(string url, string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await _http.CreateClient("images").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Remote image {Url} returned {Status}", url, (int)resp.StatusCode);
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs, cts.Token);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.LogDebug("Remote image {Url} failed: {Error}", url, ex.Message);
            return null;
        }
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}
