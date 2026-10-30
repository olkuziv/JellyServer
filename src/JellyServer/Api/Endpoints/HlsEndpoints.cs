using System.Globalization;
using JellyServer.Configuration;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Transcoding;
using Microsoft.Extensions.Options;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>
/// Jellyfin-style HLS conversion endpoints: master.m3u8 → main.m3u8 (full VOD playlist)
/// → hls1/main/{n}.ts|mp4 segments produced on demand by <see cref="TranscodeManager"/>.
/// URLs carry the ApiKey because players fetch playlists/segments without auth headers.
/// </summary>
public static class HlsEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("").RequireToken();
        g.MapGet("/Videos/{id}/master.m3u8", Master);
        g.MapGet("/Videos/{id}/main.m3u8", Main);
        g.MapGet("/Videos/{id}/hls1/{playlistId}/{segment}", Segment);
        g.MapDelete("/Videos/ActiveEncodings", (HttpContext ctx, TranscodeManager tm) =>
        {
            tm.StopSession(ctx.Request.Query.Str("playSessionId"), ctx.Request.Query.Str("deviceId"));
            return Results.NoContent();
        });
    }

    /// <summary>The URL handed to clients in PlaybackInfo.TranscodingUrl.</summary>
    public static string TranscodingUrl(Item item, TranscodePlan plan, string playSessionId, AuthInfo auth) =>
        $"/Videos/{item.Id.N()}/master.m3u8?MediaSourceId={item.Id.N()}&PlaySessionId={playSessionId}" +
        $"&DeviceId={Uri.EscapeDataString(auth.Header.DeviceId)}&ApiKey={auth.Session.Token}&{plan.ToQuery()}";

    private static string Query(HttpContext ctx) => ctx.Request.QueryString.Value?.TrimStart('?') ?? "";

    private static async Task<(Item? Item, TranscodePlan? Plan)> Resolve(HttpContext ctx, ItemRepository items)
    {
        if (ctx.RouteGuid("id") is not { } id) return (null, null);
        var plan = TranscodePlan.FromQuery(ctx.Request.Query);
        var item = plan is null ? null : await items.GetSourceAsync(id, Ids.Parse(ctx.Request.Query.Str("mediaSourceId")), ctx.RequestAborted);
        if (item is null || item.IsFolder || item.IsStrm) return (null, null);
        // The caller's policy: playback allowed at all, and the item is one they may see.
        var policy = ctx.Policy();
        var converts = !plan.CopyVideo && !policy.EnableVideoPlaybackTranscoding || !plan.CopyAudio && !policy.EnableAudioPlaybackTranscoding
                       || (plan.CopyVideo || plan.CopyAudio) && !policy.EnablePlaybackRemuxing;
        return !policy.EnableMediaPlayback || converts || !await ctx.CanAccessAsync(item) ? (null, null) : (item, plan);
    }

    private static async Task<IResult> Master(HttpContext ctx, ItemRepository items, IOptionsMonitor<ServerOptions> options)
    {
        var (item, plan) = await Resolve(ctx, items);
        if (item is null || plan is null) return Results.NotFound();
        var video = item.MediaInfo?.Video.FirstOrDefault();
        var bandwidth = plan.CopyVideo && item.MediaInfo?.Bitrate > 0 ? item.MediaInfo.Bitrate : plan.MaxBitrate ?? 8_000_000;
        var max = options.CurrentValue.Transcoding.MaxHeight;
        var resolution = video is { Width: > 0, Height: > 0 }
            ? (plan.CopyVideo || max <= 0 || video.Height <= max
                ? $",RESOLUTION={video.Width}x{video.Height}"
                : $",RESOLUTION={video.Width * max / video.Height / 2 * 2}x{max}")
            : "";
        var body = $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH={bandwidth.ToString(CultureInfo.InvariantCulture)}{resolution}\nmain.m3u8?{Query(ctx)}\n";
        return Playlist(body);
    }

    private static async Task<IResult> Main(HttpContext ctx, ItemRepository items, MediaProbe probe, TranscodeManager tm)
    {
        var (item, plan) = await Resolve(ctx, items);
        if (item is null || plan is null) return Results.NotFound();
        if (item.RuntimeTicks <= 0)
        {
            await probe.EnsureAsync(item, ctx.RequestAborted);
            item = await items.GetAsync(item.Id, ctx.RequestAborted) ?? item;
        }
        if (item.RuntimeTicks <= 0) return Results.Problem("Unknown duration; cannot build playlist", statusCode: 500);
        return Playlist(tm.BuildPlaylist(item.RuntimeTicks, plan, Query(ctx)));
    }

    private static async Task<IResult> Segment(HttpContext ctx, ItemRepository items, TranscodeManager tm)
    {
        var (item, plan) = await Resolve(ctx, items);
        if (item is null || plan is null) return Results.NotFound();
        var name = ctx.Request.RouteValues["segment"]?.ToString() ?? "";
        if (!int.TryParse(Path.GetFileNameWithoutExtension(name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) || index < -1)
            return Results.NotFound();
        var session = ctx.Request.Query.Str("playSessionId") ?? ctx.Auth().Header.DeviceId;
        var path = await tm.GetSegmentAsync(item, plan, session, ctx.Auth().Header.DeviceId, index, ctx.RequestAborted);
        if (path is null) return Results.Problem("Segment could not be produced", statusCode: 500);
        var type = plan.SegmentContainer == "mp4" ? "video/mp4" : "video/mp2t";
        return Results.File(path, type, enableRangeProcessing: true);
    }

    private static IResult Playlist(string body)
    {
        return Results.Text(body, "application/vnd.apple.mpegurl");
    }
}
