using JellyServer.Configuration;
using JellyServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace JellyServer.Tests;

public class ImageIngestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("imgingest").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>A busy picture, so it does not compress to nothing on its own.</summary>
    private static byte[] Picture(int w, int h, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, bool transparent = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, transparent ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        var rnd = new Random(7);
        using var paint = new SKPaint();
        for (var i = 0; i < 1500; i++)
        {
            paint.Color = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            canvas.DrawRect(rnd.Next(w), rnd.Next(h), rnd.Next(20, 400), rnd.Next(20, 400), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 95).ToArray();
    }

    private static SKImageInfo Info(string path)
    {
        using var codec = SKCodec.Create(path)!;
        return codec.Info;
    }

    private ImageService Service(Action<ImageOptions>? tweak = null, HttpMessageHandler? handler = null)
    {
        var options = new ServerOptions { DataDir = _dir };
        tweak?.Invoke(options.Images);
        return new ImageService(Options.Create(options), new Factory(handler ?? new Stub(_ => throw new InvalidOperationException("no network"))),
            NullLogger<ImageService>.Instance);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly ImageSize Poster = new(500, 750);

    [Fact]
    public void ShrinksALargeJpegIntoTheBox()
    {
        var original = Picture(3000, 2000);

        var packed = ImageService.Compress(original, Poster, 65, 40_000_000);

        Assert.NotNull(packed);
        Assert.Equal(".jpg", packed.Extension);
        Assert.True(packed.Data.Length < original.Length / 4, $"{packed.Data.Length} vs {original.Length}");
        using var codec = SKCodec.Create(SKData.CreateCopy(packed.Data))!;
        Assert.True(codec.Info.Width <= 500 && codec.Info.Height <= 750);
        Assert.Equal(500, codec.Info.Width); // 3:2 landscape: the width is the limit
    }

    [Fact]
    public void KeepsTransparencyAsPng()
    {
        var packed = ImageService.Compress(Picture(1600, 1600, SKEncodedImageFormat.Png, transparent: true), new ImageSize(500, 250), 65, 40_000_000);

        Assert.NotNull(packed);
        Assert.Equal(".png", packed.Extension);
        using var codec = SKCodec.Create(SKData.CreateCopy(packed.Data))!;
        Assert.NotEqual(SKAlphaType.Opaque, codec.Info.AlphaType);
        Assert.True(codec.Info.Width <= 500 && codec.Info.Height <= 250);
    }

    [Fact]
    public void NeverUpscalesASmallImage()
    {
        var packed = ImageService.Compress(Picture(200, 300), Poster, 65, 40_000_000);

        Assert.NotNull(packed);
        using var codec = SKCodec.Create(SKData.CreateCopy(packed.Data))!;
        Assert.Equal((200, 300), (codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public void RejectsGarbageAndImagesOverThePixelLimit()
    {
        Assert.Null(ImageService.Compress([1, 2, 3, 4, 5], Poster, 65, 40_000_000));
        Assert.Null(ImageService.Compress(Picture(1000, 1000), Poster, 65, 500_000));
    }

    [Fact]
    public async Task UploadStoresOnlyTheCompressedImage()
    {
        var original = Picture(3000, 2000);
        var images = Service();

        var path = await images.SaveUploadAsync(Guid.NewGuid(), "Primary", ".jpg", original, CancellationToken.None);

        Assert.True(new FileInfo(path).Length < original.Length / 4);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no original, no temp file next to it
        Assert.True(Info(path).Width <= 500);
    }

    [Fact]
    public async Task UploadKeepsTheOriginalOnlyWhenAskedTo()
    {
        var original = Picture(1500, 1000);
        var images = Service(o => o.Ingest.KeepOriginal = true);

        var path = await images.SaveUploadAsync(Guid.NewGuid(), "Primary", ".jpg", original, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UploadOfSomethingThatIsNotAnImageIsRejectedAndNothingIsStored()
    {
        var images = Service();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            images.SaveUploadAsync(Guid.NewGuid(), "Primary", ".jpg", [9, 9, 9, 9], CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_dir, "images", "uploads")) && Directory.GetFiles(Path.Combine(_dir, "images", "uploads"), "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task RemoteImageIsStoredCompressedAndFetchedOnce()
    {
        var original = Picture(3000, 2000);
        var handler = new Stub(_ => new HttpResponseMessage { Content = new ByteArrayContent(original) });
        var images = Service(handler: handler);
        var req = new ImageService.ImageRequest(null, null, null, null, null, "Backdrop");

        var first = await images.GetAsync("https://img.example/p/backdrop.jpg", req, CancellationToken.None);
        var second = await images.GetAsync("https://img.example/p/backdrop.jpg", req, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
        Assert.True(Info(first).Width <= 1024 && Info(first).Height <= 576);
        var stored = Directory.GetFiles(Path.Combine(_dir, "cache", "images"), "*", SearchOption.AllDirectories);
        Assert.All(stored, f => Assert.True(new FileInfo(f).Length < original.Length / 2, $"{f} looks like the original"));
    }

    [Fact]
    public async Task RemoteImageOverTheDownloadLimitIsSkipped()
    {
        var handler = new Stub(_ => new HttpResponseMessage { Content = new ByteArrayContent(new byte[2 * 1024 * 1024]) });
        var images = Service(o => o.Ingest.MaxDownloadMB = 1, handler);

        var path = await images.GetAsync("https://img.example/p/big.jpg", new ImageService.ImageRequest(null, null, null, null, null, "Primary"), CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "cache", "images"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RemoteImageThatCannotBeDecodedIsNotStoredAsIs()
    {
        var handler = new Stub(_ => new HttpResponseMessage { Content = new ByteArrayContent([1, 2, 3, 4]) });
        var images = Service(handler: handler);

        var path = await images.GetAsync("https://img.example/p/bad.jpg", new ImageService.ImageRequest(null, null, null, null, null, "Primary"), CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "cache", "images"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void EvictionDropsTheLeastRecentlyUsedFilesFirst()
    {
        var cache = Path.Combine(_dir, "evict");
        Directory.CreateDirectory(cache);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            var f = Path.Combine(cache, $"f{i}.jpg");
            File.WriteAllBytes(f, new byte[1000]);
            File.SetLastWriteTimeUtc(f, now.AddHours(-(10 - i))); // f0 is the oldest
        }
        var stale = Path.Combine(cache, "old.jpg.tmp");
        File.WriteAllBytes(stale, new byte[10]);
        File.SetLastWriteTimeUtc(stale, now.AddHours(-3));
        var fresh = Path.Combine(cache, "new.jpg.tmp");
        File.WriteAllBytes(fresh, new byte[10]);

        var freed = ImageCacheJanitor.Evict(cache, 6000);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        // 10 KB is over 6 KB: evict down to 90% of the limit (5.4 KB) → five files left, the newest ones.
        Assert.Equal(["f5.jpg", "f6.jpg", "f7.jpg", "f8.jpg", "f9.jpg", "new.jpg.tmp"],
            Directory.GetFiles(cache).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(5 * 1000 + 10, freed);
    }

    [Fact]
    public void EvictionLeavesACacheUnderTheLimitAlone()
    {
        var cache = Path.Combine(_dir, "small");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "a.jpg"), new byte[100]);

        Assert.Equal(0, ImageCacheJanitor.Evict(cache, 10_000));
        Assert.Single(Directory.GetFiles(cache));
    }
}
