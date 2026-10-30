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
    /// <summary>Lowest quality a setting can ask for; below it pictures fall apart. WebP stops at 99 because 100 means lossless there.</summary>
    private const int MinQuality = 10;

    private readonly SemaphoreSlim _cpu;
    // Compression of stored artwork runs apart from client resizes: a weak server should not stall streaming for it.
    private readonly SemaphoreSlim _ingest;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    public ImageService(IOptions<ServerOptions> options, IHttpClientFactory http, ILogger<ImageService> log)
    {
        _opt = options.Value.Images;
        _cacheDir = _opt.CacheDir ?? Path.Combine(options.Value.DataDir, "cache", "images");
        Directory.CreateDirectory(_cacheDir);
        _uploadDir = Path.GetFullPath(Path.Combine(options.Value.DataDir, "images", "uploads")) + Path.DirectorySeparatorChar;
        _ingest = new SemaphoreSlim(Math.Max(1, _opt.Ingest.MaxConcurrency));
        _cpu = new SemaphoreSlim(Math.Max(1, _opt.MaxResizeConcurrency));
        _http = http;
        _log = log;
    }

    public string CacheDir => _cacheDir;

    /// <summary>Stored artwork is compressed unless ingest is off or the originals are kept explicitly.</summary>
    private bool Compressing => _opt.Ingest is { Enabled: true, KeepOriginal: false };

    /// <summary><paramref name="Kind"/> is the image type (Primary, Backdrop, ...) and picks the size limit for stored artwork.</summary>
    /// <param name="AcceptsWebp">The client can show WebP: it gets WebP variants, and a stored WebP file as it is. Anyone else gets JPEG/PNG.</param>
    public sealed record ImageRequest(int? MaxWidth, int? MaxHeight, int? FillWidth, int? FillHeight, int? Quality, string? Kind = null, bool AcceptsWebp = false);

    public static bool IsWebp(string path) => path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a local file path for the requested variant, or null when unavailable.</summary>
    public async Task<string?> GetAsync(string source, ImageRequest req, CancellationToken ct)
    {
        var original = await ResolveSourceAsync(source, req.Kind, ct);
        if (original is null) return null;

        // A WebP file must not reach a client that cannot show it, whatever the resize setting is.
        var mustConvert = IsWebp(original) && !req.AcceptsWebp;
        var (w, h) = (req.FillWidth ?? req.MaxWidth, req.FillHeight ?? req.MaxHeight);
        var resize = _opt.Resize && (w is not null || h is not null);
        if (!resize && !mustConvert) return original;

        // Round to 50px buckets: far fewer variants on disk, visually identical.
        w = resize && w is { } ww ? Bucket(ww) : null;
        h = resize && h is { } hh ? Bucket(hh) : null;
        // WebP variants only when the client takes them; JPEG/PNG keep their old cache keys, so files cached before stay valid.
        var webp = req.AcceptsWebp;
        var quality = webp
            ? Math.Clamp(Math.Min(req.Quality ?? _opt.WebpQuality, _opt.WebpQuality), MinQuality, 99)
            : Math.Clamp(req.Quality ?? _opt.JpegQuality, MinQuality, 100);
        var info = new FileInfo(original);
        var key = Hash($"{original}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{w}|{h}|{quality}{(webp ? "|webp" : "")}");
        var basePath = Path.Combine(_cacheDir, key[..2], key);
        // The extension of a JPEG/PNG variant is only known after decoding (alpha decides).
        string[] candidates = webp ? [basePath + ".webp"] : [basePath + ".jpg", basePath + ".png"];
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            ImageCacheJanitor.Touch(c);
            return c;
        }

        var lazy = _inflight.GetOrAdd(basePath, p => new Lazy<Task<string?>>(() => ResizeAsync(original, p, w, h, quality, webp)));
        try { return await lazy.Value; }
        finally { _inflight.TryRemove(basePath, out _); }
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
        if (Compressing)
        {
            var packed = await CompressAsync(data, type, ct)
                ?? throw new InvalidDataException("The image could not be decoded or is too large");
            (data, extension) = (packed.Data, packed.Extension);
        }
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

    /// <summary>
    /// Writes the resized (and/or converted) variant next to <paramref name="basePath"/> and returns its path; the source itself when
    /// there is nothing to do or it cannot be decoded. The picture is decoded at the smallest size that still covers the target
    /// (JPEG and WebP can do that natively), so a 4K poster is never held in memory at full size just to make a 300px thumbnail.
    /// </summary>
    private async Task<string?> ResizeAsync(string source, string basePath, int? maxW, int? maxH, int quality, bool webp)
    {
        await _cpu.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var codec = SKCodec.Create(source);
                if (codec is null) return source; // undecodable: serve as-is
                var info = codec.Info;
                var (sw, sh) = (info.Width, info.Height);
                var scale = Math.Min(maxW is { } mw ? (double)mw / sw : double.MaxValue, maxH is { } mh ? (double)mh / sh : double.MaxValue);
                var shrink = scale < 1; // never upscale
                // Nothing to shrink, and the file is already something this client can show.
                if (!shrink && (webp || !IsWebp(source))) return source;
                var (tw, th) = shrink ? (Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale))) : (sw, sh);

                var decodeSize = new SKSizeI(sw, sh);
                if (shrink)
                {
                    var scaled = codec.GetScaledDimensions((float)scale);
                    if (scaled.Width >= tw && scaled.Height >= th) decodeSize = scaled;
                }
                using var decoded = SKBitmap.Decode(codec, info.WithSize(decodeSize.Width, decodeSize.Height));
                if (decoded is null) return source;

                SKBitmap? resized = null;
                try
                {
                    var bitmap = decoded;
                    if (shrink && (decoded.Width != tw || decoded.Height != th))
                    {
                        bitmap = resized = decoded.Resize(new SKImageInfo(tw, th, decoded.ColorType, decoded.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        if (bitmap is null) return source;
                    }
                    var alpha = info.AlphaType != SKAlphaType.Opaque;
                    var (format, ext) = webp ? (SKEncodedImageFormat.Webp, ".webp") : alpha ? (SKEncodedImageFormat.Png, ".png") : (SKEncodedImageFormat.Jpeg, ".jpg");
                    var target = basePath + ext;

                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(format, quality);
                    if (data is null) return source;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var tmp = target + "." + Environment.CurrentManagedThreadId + ".tmp";
                    using (var fs = File.Create(tmp)) data.SaveTo(fs);
                    File.Move(tmp, target, overwrite: true); // atomic publish
                    return target;
                }
                finally
                {
                    resized?.Dispose();
                }
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
    private async Task<string?> ResolveSourceAsync(string source, string? kind, CancellationToken ct)
    {
        if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return File.Exists(source) ? source : null;
        if (!_opt.RemoteFetch) return null;

        var compress = Compressing;
        // Compressed copies depend on the image type (size limit), originals do not.
        var key = Hash(compress ? $"{source}|{kind}" : source);
        var basePath = Path.Combine(_cacheDir, "remote", key[..2], key);
        // The extension of a compressed copy is only known after decoding (alpha decides PNG vs JPEG).
        var legacyExt = Path.GetExtension(new Uri(source).AbsolutePath) is { Length: > 1 and <= 5 } e ? e.ToLowerInvariant() : ".jpg";
        // Copies stored as JPEG/PNG before WebP became the default stay valid.
        string[] candidates = compress ? [basePath + ".webp", basePath + ".jpg", basePath + ".png"] : [basePath + legacyExt];
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            ImageCacheJanitor.Touch(c);
            return c;
        }

        var lazy = _inflight.GetOrAdd(basePath, p => new Lazy<Task<string?>>(() => DownloadAsync(source, p, legacyExt, kind, compress)));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { _inflight.TryRemove(basePath, out _); }
    }

    private async Task<string?> DownloadAsync(string url, string basePath, string legacyExt, string? kind, bool compress)
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
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

            if (!compress)
            {
                var path = basePath + legacyExt;
                var tmp = path + ".tmp";
                await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs, cts.Token);
                File.Move(tmp, path, overwrite: true);
                return path;
            }

            // The original is held in memory only: it never reaches the disk, just the compressed result does.
            var limit = (long)Math.Max(1, _opt.Ingest.MaxDownloadMB) * 1024 * 1024;
            if (resp.Content.Headers.ContentLength > limit)
            {
                _log.LogWarning("Remote image {Url} is {Size} bytes, over the {Limit} MB ingest limit; skipped", url, resp.Content.Headers.ContentLength, _opt.Ingest.MaxDownloadMB);
                return null;
            }
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cts.Token)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    _log.LogWarning("Remote image {Url} is over the {Limit} MB ingest limit; skipped", url, _opt.Ingest.MaxDownloadMB);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }

            var packed = await CompressAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), kind, cts.Token);
            if (packed is null)
            {
                _log.LogWarning("Remote image {Url} could not be decoded or is too large; skipped", url);
                return null;
            }
            var target = basePath + packed.Extension;
            var tmpFile = target + ".tmp";
            await File.WriteAllBytesAsync(tmpFile, packed.Data, cts.Token);
            File.Move(tmpFile, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.LogDebug("Remote image {Url} failed: {Error}", url, ex.Message);
            return null;
        }
    }

    public sealed record Compressed(byte[] Data, string Extension);

    /// <summary>Compresses stored artwork on the bounded ingest queue; null when the image cannot be used.</summary>
    private async Task<Compressed?> CompressAsync(ReadOnlyMemory<byte> data, string? kind, CancellationToken ct)
    {
        var ing = _opt.Ingest;
        var box = ing.SizeFor(kind);
        var quality = ing.Webp ? Math.Clamp(ing.WebpQuality, MinQuality, 99) : Math.Clamp(ing.JpegQuality, MinQuality, 100);
        await _ingest.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Compress(data, box, quality, ing.MaxPixels, ing.Webp), ct);
        }
        finally
        {
            _ingest.Release();
        }
    }

    /// <summary>
    /// Shrinks an image into <paramref name="box"/> and re-encodes it: as WebP when <paramref name="webp"/> is set (transparency
    /// included), otherwise PNG when it has transparency and JPEG when not.
    /// JPEGs are decoded already scaled down, so a large picture is never held in memory at full size.
    /// Returns null for data that is not an image or has more than <paramref name="maxPixels"/> pixels.
    /// </summary>
    public static Compressed? Compress(byte[] data, ImageSize box, int quality, int maxPixels, bool webp = false) =>
        Compress(data.AsMemory(), box, quality, maxPixels, webp);

    /// <summary>The same for bytes that are part of a larger buffer, so the caller does not copy them first.</summary>
    public static Compressed? Compress(ReadOnlyMemory<byte> data, ImageSize box, int quality, int maxPixels, bool webp = false)
    {
        try
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(data.Span));
            if (codec is null) return null;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > maxPixels) return null;

            var keepAlpha = info.AlphaType != SKAlphaType.Opaque;
            var outFormat = webp ? SKEncodedImageFormat.Webp : keepAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
            var ext = webp ? ".webp" : keepAlpha ? ".png" : ".jpg";
            var scale = Math.Min(1.0, Math.Min((double)Math.Max(1, box.Width) / info.Width, (double)Math.Max(1, box.Height) / info.Height));
            var (tw, th) = (Math.Max(1, (int)Math.Round(info.Width * scale)), Math.Max(1, (int)Math.Round(info.Height * scale)));
            var resize = scale < 1;

            var decodeSize = new SKSizeI(info.Width, info.Height);
            if (resize)
            {
                var scaled = codec.GetScaledDimensions((float)scale);
                if (scaled.Width >= tw && scaled.Height >= th) decodeSize = scaled;
            }
            using var decoded = SKBitmap.Decode(codec, info.WithSize(decodeSize.Width, decodeSize.Height));
            if (decoded is null) return null;

            SKBitmap? resized = null;
            try
            {
                var bitmap = decoded;
                if (resize && (decoded.Width != tw || decoded.Height != th))
                    bitmap = resized = decoded.Resize(new SKImageInfo(tw, th, decoded.ColorType, decoded.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (bitmap is null) return null;

                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(outFormat, quality);
                if (encoded is null) return null;
                var bytes = encoded.ToArray();

                // Already inside the limits and the re-encode did not help: the input is as small as it gets, in whatever format it has.
                if (!resize && bytes.Length >= data.Length && ExtensionOf(codec.EncodedFormat) is { } own) return new Compressed(data.ToArray(), own);
                return new Compressed(bytes, ext);
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExtensionOf(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ".jpg",
        SKEncodedImageFormat.Png => ".png",
        SKEncodedImageFormat.Webp => ".webp",
        _ => null,
    };

    private static string Hash(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}
