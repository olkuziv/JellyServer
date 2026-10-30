using JellyServer.Configuration;
using JellyServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace JellyServer.Tests;

public class ImageWebpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("imgwebp").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] Picture(int w, int h, SKEncodedImageFormat format, bool transparent = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, transparent ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        var rnd = new Random(7);
        using var paint = new SKPaint();
        for (var i = 0; i < 400; i++)
        {
            paint.Color = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            canvas.DrawRect(rnd.Next(w), rnd.Next(h), rnd.Next(20, 300), rnd.Next(20, 300), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    private static SKImageInfo Info(string path)
    {
        using var codec = SKCodec.Create(path)!;
        return codec.Info;
    }

    private static SKEncodedImageFormat FormatOf(string path)
    {
        using var codec = SKCodec.Create(path)!;
        return codec.EncodedFormat;
    }

    private ImageService Service(Action<ServerOptions>? tweak = null)
    {
        var options = new ServerOptions { DataDir = _dir };
        tweak?.Invoke(options);
        return new ImageService(Options.Create(options), new Factory(), NullLogger<ImageService>.Instance);
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network");
    }

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static ImageService.ImageRequest Request(int? maxWidth = null, bool webp = false) =>
        new(maxWidth, null, null, null, null, "Primary", webp);

    [Fact]
    public void CompressesToWebpAndKeepsTransparency()
    {
        var opaque = ImageService.Compress(Picture(2000, 3000, SKEncodedImageFormat.Jpeg), new ImageSize(500, 750), 70, 40_000_000, webp: true);
        var clear = ImageService.Compress(Picture(1600, 1600, SKEncodedImageFormat.Png, transparent: true), new ImageSize(500, 250), 70, 40_000_000, webp: true);

        Assert.NotNull(opaque);
        Assert.Equal(".webp", opaque.Extension);
        Assert.NotNull(clear);
        Assert.Equal(".webp", clear.Extension); // no PNG any more
        using var codec = SKCodec.Create(SKData.CreateCopy(clear.Data))!;
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.NotEqual(SKAlphaType.Opaque, codec.Info.AlphaType);
        Assert.True(codec.Info.Width <= 500 && codec.Info.Height <= 250);
    }

    [Fact]
    public async Task UploadsAreStoredAsWebpByDefaultAndAsJpegWhenSwitchedOff()
    {
        var original = Picture(3000, 2000, SKEncodedImageFormat.Jpeg);

        var webp = await Service().SaveUploadAsync(Guid.NewGuid(), "Primary", ".jpg", original, CancellationToken.None);
        var jpeg = await Service(o => o.Images.Ingest.Webp = false).SaveUploadAsync(Guid.NewGuid(), "Primary", ".jpg", original, CancellationToken.None);

        Assert.EndsWith(".webp", webp);
        Assert.Equal(SKEncodedImageFormat.Webp, FormatOf(webp));
        Assert.EndsWith(".jpg", jpeg);
    }

    [Fact]
    public async Task AWebpFileGoesAsItIsToClientsThatTakeWebp()
    {
        var path = Write("poster.webp", Picture(600, 900, SKEncodedImageFormat.Webp));

        var served = await Service().GetAsync(path, Request(webp: true), CancellationToken.None);

        Assert.Equal(path, served);
    }

    [Fact]
    public async Task AWebpFileBecomesJpegForOtherClientsEvenWithoutAResize()
    {
        var path = Write("poster.webp", Picture(600, 900, SKEncodedImageFormat.Webp));

        var served = await Service().GetAsync(path, Request(), CancellationToken.None);

        Assert.NotNull(served);
        Assert.NotEqual(path, served);
        Assert.Equal(SKEncodedImageFormat.Jpeg, FormatOf(served));
        Assert.Equal((600, 900), (Info(served).Width, Info(served).Height));
    }

    [Fact]
    public async Task AWebpWithTransparencyBecomesPngForOtherClients()
    {
        var path = Write("logo.webp", Picture(400, 200, SKEncodedImageFormat.Webp, transparent: true));

        var served = await Service().GetAsync(path, Request(), CancellationToken.None);

        Assert.NotNull(served);
        Assert.Equal(SKEncodedImageFormat.Png, FormatOf(served));
    }

    [Fact]
    public async Task ResizedVariantsAreWebpOnlyForClientsThatTakeIt()
    {
        var path = Write("poster.jpg", Picture(2000, 3000, SKEncodedImageFormat.Jpeg));
        var images = Service();

        var webp = await images.GetAsync(path, Request(maxWidth: 300, webp: true), CancellationToken.None);
        var jpeg = await images.GetAsync(path, Request(maxWidth: 300), CancellationToken.None);

        Assert.NotNull(webp);
        Assert.NotNull(jpeg);
        Assert.Equal(SKEncodedImageFormat.Webp, FormatOf(webp));
        Assert.Equal(SKEncodedImageFormat.Jpeg, FormatOf(jpeg));
        Assert.NotEqual(webp, jpeg);
        Assert.True(Info(webp).Width <= 300 && Info(jpeg).Width <= 300);
    }

    [Fact]
    public async Task ALibraryJpegIsServedUntouchedWhenNoResizeIsAsked()
    {
        var path = Write("folder.jpg", Picture(800, 1200, SKEncodedImageFormat.Jpeg));
        var images = Service();

        Assert.Equal(path, await images.GetAsync(path, Request(webp: true), CancellationToken.None));
        Assert.Equal(path, await images.GetAsync(path, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task AClientsQualityOnlyLowersTheWebpQuality()
    {
        var path = Write("poster.jpg", Picture(2000, 3000, SKEncodedImageFormat.Jpeg));
        var images = Service();

        var capped = await images.GetAsync(path, new ImageService.ImageRequest(300, null, null, null, 100, "Primary", true), CancellationToken.None);
        var configured = await images.GetAsync(path, new ImageService.ImageRequest(300, null, null, null, 80, "Primary", true), CancellationToken.None);

        // 100 would be lossless in WebP; it is capped to the configured quality, which is what 80 asks for as well.
        Assert.Equal(capped, configured);
    }
}
