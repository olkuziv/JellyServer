using JellyServer.Configuration;
using JellyServer.Serialization;
using Microsoft.Extensions.Options;

namespace JellyServer.Api.Endpoints;

/// <summary>A font in the fallback font folder (Jellyfin's MediaBrowser.Model.Subtitles.FontFile).</summary>
public sealed class FontFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime DateCreated { get; set; }
    public DateTime DateModified { get; set; }
}

/// <summary>
/// Jellyfin's fallback fonts (SubtitleController): jellyfin-web lists them when EnableFallbackFont is on and hands
/// them to its ASS renderer (SubtitlesOctopus) for glyphs the subtitle's own fonts lack.
/// </summary>
public static class FallbackFontEndpoints
{
    /// <summary>Jellyfin stops listing once the fonts add up to 20 MiB: clients download all of them.</summary>
    public const long MaxTotalSize = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> FontTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
    };

    public static void Map(WebApplication app)
    {
        // [Authorize] in Jellyfin; jellyfin-web passes ApiKey in the query.
        var g = app.MapGroup("/FallbackFont").RequireToken();
        g.MapGet("/Fonts", (IOptionsMonitor<ServerOptions> o, ILogger<FontFile> log) =>
            Results.Json(List(o.CurrentValue.Transcoding.FallbackFont, log), AppJson.Default.ListFontFile));
        g.MapGet("/Fonts/{name}", (string name, IOptionsMonitor<ServerOptions> o, ILogger<FontFile> log) =>
            Get(o.CurrentValue.Transcoding.FallbackFont, name, log));
    }

    /// <summary>Fonts smallest first, cut off at <see cref="MaxTotalSize"/>; empty when disabled or the folder is missing.</summary>
    public static List<FontFile> List(FallbackFontOptions opt, ILogger log)
    {
        if (Folder(opt, log) is not { } dir) return [];
        var fonts = Fonts(dir)
            .Select(f => new FontFile { Name = f.Name, Size = f.Length, DateCreated = f.CreationTimeUtc, DateModified = f.LastWriteTimeUtc })
            .OrderBy(f => f.Size).ThenBy(f => f.Name, StringComparer.Ordinal)
            .ThenByDescending(f => f.DateModified).ThenByDescending(f => f.DateCreated);
        var result = new List<FontFile>();
        var total = 0L;
        foreach (var f in fonts)
        {
            total += f.Size;
            if (total >= MaxTotalSize)
            {
                log.LogWarning("Some fallback fonts will not be sent due to size limitations");
                break;
            }
            result.Add(f);
        }
        return result;
    }

    /// <summary>
    /// One font by file name. The name is only compared against the folder listing, never joined into a path,
    /// so "../" and absolute names cannot escape the folder.
    /// </summary>
    public static IResult Get(FallbackFontOptions opt, string name, ILogger log)
    {
        if (Folder(opt, log) is not { } dir) return Results.Ok(); // Jellyfin: an empty 200, a 204 breaks SubtitlesOctopus
        var font = Fonts(dir).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (font is null) return Results.NotFound();
        if (font.Length == 0)
        {
            log.LogWarning("The selected fallback font {Font} is empty", font.Name);
            return Results.Ok();
        }
        return Results.File(font.FullName, FontTypes[font.Extension]);
    }

    private static DirectoryInfo? Folder(FallbackFontOptions opt, ILogger log)
    {
        if (!opt.Enabled) return null;
        if (string.IsNullOrWhiteSpace(opt.Path) || !Directory.Exists(opt.Path))
        {
            log.LogWarning("The fallback font folder {Path} is not set or does not exist", opt.Path);
            return null;
        }
        return new DirectoryInfo(opt.Path);
    }

    private static IEnumerable<FileInfo> Fonts(DirectoryInfo dir) =>
        dir.EnumerateFiles().Where(f => FontTypes.ContainsKey(f.Extension));
}
