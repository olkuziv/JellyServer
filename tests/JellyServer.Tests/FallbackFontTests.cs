using System.Text.Json;
using JellyServer.Api.Endpoints;
using JellyServer.Configuration;
using JellyServer.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyServer.Tests;

public sealed class FallbackFontTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jelly-fonts-" + Guid.NewGuid().ToString("N"));
    private readonly string _fonts;

    public FallbackFontTests()
    {
        _fonts = Path.Combine(_dir, "fonts");
        Directory.CreateDirectory(_fonts);
        File.WriteAllBytes(Path.Combine(_fonts, "Big.ttf"), new byte[300]);
        File.WriteAllBytes(Path.Combine(_fonts, "small.woff2"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_fonts, "Mid.OTF"), new byte[200]);
        File.WriteAllBytes(Path.Combine(_fonts, "readme.txt"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_fonts, "empty.woff"), []);
        File.WriteAllBytes(Path.Combine(_dir, "secret.ttf"), new byte[50]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private FallbackFontOptions Enabled => new() { Enabled = true, Path = _fonts };

    [Fact]
    public void List_FontsOnly_SmallestFirst()
    {
        var list = FallbackFontEndpoints.List(Enabled, NullLogger.Instance);
        Assert.Equal(["empty.woff", "small.woff2", "Mid.OTF", "Big.ttf"], list.Select(f => f.Name));
        Assert.Equal([0L, 100, 200, 300], list.Select(f => f.Size));
        Assert.All(list, f => Assert.Equal(DateTimeKind.Utc, f.DateModified.Kind));
    }

    [Fact]
    public void List_SerializesLikeJellyfin()
    {
        var json = JsonSerializer.Serialize(FallbackFontEndpoints.List(Enabled, NullLogger.Instance), AppJson.Default.ListFontFile);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        Assert.Equal(["Name", "Size", "DateCreated", "DateModified"], first.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void List_EmptyWhenDisabledOrFolderMissing(bool enabled, bool exists)
    {
        var opt = new FallbackFontOptions { Enabled = enabled, Path = exists ? _fonts : Path.Combine(_dir, "nope") };
        Assert.Empty(FallbackFontEndpoints.List(opt, NullLogger.Instance));
        Assert.Empty(FallbackFontEndpoints.List(new FallbackFontOptions { Enabled = true }, NullLogger.Instance));
    }

    [Fact]
    public void List_StopsAt20MiB()
    {
        File.WriteAllBytes(Path.Combine(_fonts, "huge1.ttf"), new byte[12 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(_fonts, "huge2.ttf"), new byte[12 * 1024 * 1024]);
        var names = FallbackFontEndpoints.List(Enabled, NullLogger.Instance).Select(f => f.Name).ToList();
        Assert.Contains("huge1.ttf", names);
        Assert.DoesNotContain("huge2.ttf", names);
    }

    [Fact]
    public void Get_ServesFont_CaseInsensitive_WithFontType()
    {
        var r = Assert.IsType<PhysicalFileHttpResult>(FallbackFontEndpoints.Get(Enabled, "mid.otf", NullLogger.Instance));
        Assert.Equal(Path.Combine(_fonts, "Mid.OTF"), r.FileName);
        Assert.Equal("font/otf", r.ContentType);
        Assert.Equal("font/woff2", Assert.IsType<PhysicalFileHttpResult>(FallbackFontEndpoints.Get(Enabled, "small.woff2", NullLogger.Instance)).ContentType);
    }

    [Theory]
    [InlineData("../secret.ttf")]
    [InlineData("..\\secret.ttf")]
    [InlineData("secret.ttf")]
    [InlineData("readme.txt")]
    [InlineData("missing.ttf")]
    public void Get_OnlyFontsInsideTheFolder(string name) =>
        Assert.IsType<NotFound>(FallbackFontEndpoints.Get(Enabled, name, NullLogger.Instance));

    [Fact]
    public void Get_AbsolutePathIsNotFollowed() =>
        Assert.IsType<NotFound>(FallbackFontEndpoints.Get(Enabled, Path.Combine(_dir, "secret.ttf"), NullLogger.Instance));

    [Fact]
    public void Get_EmptyOrDisabled_IsAnEmpty200_LikeJellyfin()
    {
        Assert.IsType<Ok>(FallbackFontEndpoints.Get(Enabled, "empty.woff", NullLogger.Instance));
        Assert.IsType<Ok>(FallbackFontEndpoints.Get(new FallbackFontOptions { Path = _fonts }, "Big.ttf", NullLogger.Instance));
    }
}
