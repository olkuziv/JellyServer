using System.Net.WebSockets;
using System.Text.Json;
using JellyServer.Configuration;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Transcoding;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.UserData.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Playback negotiation, streaming, subtitles, progress reporting and the client WebSocket.</summary>
public static class PlaybackEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new()
    {
        Mappings = { [".mkv"] = "video/x-matroska", [".ts"] = "video/mp2t", [".m2ts"] = "video/mp2t", [".webm"] = "video/webm", [".avi"] = "video/x-msvideo" },
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("").RequireToken();
        g.MapMethods("/Items/{id}/PlaybackInfo", ["GET", "POST"], PlaybackInfo);
        g.MapPost("/Sessions/Playing", (HttpContext c, IUserDataStore u, ItemRepository i, SessionRegistry s, IOptions<ServerOptions> o) => Report(c, u, i, s, o, ReportKind.Start));
        g.MapPost("/Sessions/Playing/Progress", (HttpContext c, IUserDataStore u, ItemRepository i, SessionRegistry s, IOptions<ServerOptions> o, TranscodeManager tm) =>
            Report(c, u, i, s, o, ReportKind.Progress, tm));
        g.MapPost("/Sessions/Playing/Stopped", (HttpContext c, IUserDataStore u, ItemRepository i, SessionRegistry s, IOptions<ServerOptions> o, TranscodeManager tm) =>
            Report(c, u, i, s, o, ReportKind.Stopped, tm));
        g.MapPost("/Sessions/Playing/Ping", () => Results.NoContent());
        g.MapPost("/Sessions/Capabilities", () => Results.NoContent());
        g.MapPost("/Sessions/Capabilities/Full", () => Results.NoContent());
        g.MapGet("/Sessions", Sessions);

        // Streams authenticate via ApiKey in the query (players cannot set headers).
        g.MapMethods("/Videos/{id}/{file}", ["GET", "HEAD"], Stream);
        g.MapMethods("/Items/{id}/Download", ["GET", "HEAD"], Stream);
        // Proxied segments carry an HMAC signature instead of a token.
        app.MapMethods("/Videos/{id}/proxy", ["GET", "HEAD"], Proxy);
        // Subtitles are anonymous in Jellyfin too; the custom renderer fetches them without headers.
        app.MapGet("/Videos/{id}/{msid}/Subtitles/{index:int}/{file}", Subtitle);
        app.MapGet("/Videos/{id}/{msid}/Subtitles/{index:int}/{ticks:long}/{file}", Subtitle);

        app.Map("/socket", Socket);
    }

    /// <summary>
    /// Negotiates playback: direct play when the client's DeviceProfile supports the file,
    /// otherwise an HLS conversion URL (remux when the video codec is supported, else transcode).
    /// </summary>
    private static async Task<IResult> PlaybackInfo(HttpContext ctx, ItemRepository items, DtoBuilder dto, MediaProbe probe,
        PipelineSelector pipelines, IOptionsMonitor<ServerOptions> options, ILogger<DtoBuilder> log)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetSourceAsync(id, Ids.Parse(ctx.Request.Query.Str("mediaSourceId")), ctx.RequestAborted) is not { } item || item.IsFolder)
            return Results.NotFound();
        var req = (HttpMethods.IsPost(ctx.Request.Method) ? await ctx.Request.ReadBodyAsync(AppJson.Default.PlaybackInfoRequest) : null) ?? new PlaybackInfoRequest();
        var q = ctx.Request.Query;
        // The version the client picked (a body field or a query parameter); the main item's file otherwise.
        if ((req.MediaSourceId ?? q.Str("mediaSourceId")) is { } wantedSource && Ids.Parse(wantedSource) is { } wantedId && wantedId != item.Id)
            item = await items.GetSourceAsync(item.PrimaryId ?? item.Id, wantedId, ctx.RequestAborted) ?? item;
        req.AudioStreamIndex ??= q.Int("audioStreamIndex");
        req.MaxStreamingBitrate ??= q.Long("maxStreamingBitrate");
        req.EnableDirectPlay ??= q.Bool("enableDirectPlay");
        req.EnableDirectStream ??= q.Bool("enableDirectStream");
        req.EnableTranscoding ??= q.Bool("enableTranscoding");
        req.AllowVideoStreamCopy ??= q.Bool("allowVideoStreamCopy");
        req.AllowAudioStreamCopy ??= q.Bool("allowAudioStreamCopy");

        var auth = ctx.Auth();
        var playSessionId = Guid.NewGuid().ToString("N");
        var transcoding = options.CurrentValue.Transcoding.Enabled && pipelines.IsAvailable;
        if (!item.IsStrm && transcoding && req.DeviceProfile is not null)
            await probe.EnsureAsync(item, ctx.RequestAborted); // real codecs are needed for the decision

        var source = dto.MediaSource(item);
        var decision = item.IsStrm
            ? new PlaybackDecision(true, null, $"strm ({options.CurrentValue.Playback.StrmMode})")
            : PlaybackDecider.Decide(item, item.MediaInfo, req, transcoding, pipelines.EncodableCodecs,
                options.CurrentValue.Transcoding.AllowHevcEncoding, options.CurrentValue.Transcoding.AllowAv1Encoding);
        if (decision.Plan is { } plan)
        {
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = false;
            source.SupportsTranscoding = true;
            source.TranscodingSubProtocol = "hls";
            source.TranscodingContainer = plan.SegmentContainer;
            source.TranscodingUrl = HlsEndpoints.TranscodingUrl(item, plan, playSessionId, auth);
        }
        if (req.AudioStreamIndex is { } ai) source.DefaultAudioStreamIndex = ai;
        dto.ApplySubtitleDelivery(source, item, req.DeviceProfile?.SubtitleProfiles, decision.Plan is not null,
            decision.Plan?.SegmentContainer ?? item.Container);

        log.LogInformation("PlaybackInfo {Item} for {Client}/{Device}: {Decision}", item.Name, auth.Header.Client, auth.Header.Device, decision.Reason);
        return Results.Json(new PlaybackInfoResponse { MediaSources = [source], PlaySessionId = playSessionId },
            AppJson.Default.PlaybackInfoResponse);
    }

    private static async Task<IResult> Stream(HttpContext ctx, ItemRepository items, StreamProxy proxy, IOptions<ServerOptions> options)
    {
        var file = ctx.Request.RouteValues["file"]?.ToString();
        if (file is not null && !file.StartsWith("stream", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        if (ctx.RouteGuid("id") is not { } id || await items.GetSourceAsync(id, Ids.Parse(ctx.Request.Query.Str("mediaSourceId")), ctx.RequestAborted) is not { } item || item.IsFolder)
            return Results.NotFound();

        if (item.IsStrm)
        {
            if (options.Value.Playback.StrmMode == "proxy")
            {
                await proxy.RelayAsync(ctx, item.Id, item.StreamUrl!);
                return Results.Empty;
            }
            return Results.Redirect(item.StreamUrl!);
        }
        if (!File.Exists(item.Path)) return Results.NotFound();
        var contentType = ContentTypes.TryGetContentType(item.Path, out var ct) ? ct : "application/octet-stream";
        // Kernel sendfile + HTTP range support: seeking never reads more than needed.
        return Results.File(item.Path, contentType, enableRangeProcessing: true);
    }

    private static async Task<IResult> Proxy(HttpContext ctx, ItemRepository items, StreamProxy proxy)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        var url = proxy.VerifySigned(id, ctx.Request.Query.Str("u"), ctx.Request.Query.Str("s"));
        if (url is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
        await proxy.RelayAsync(ctx, id, url);
        return Results.Empty;
    }

    /// <summary>
    /// Jellyfin's GET Videos/{id}/{msid}/Subtitles/{index}[/{ticks}]/Stream.{format}: index is the MediaStreams index.
    /// Sidecar files are converted in memory; embedded streams are extracted with ffmpeg and cached on disk.
    /// </summary>
    private static async Task<IResult> Subtitle(HttpContext ctx, ItemRepository items, SubtitleExtractor extractor, MediaProbe probe)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetSourceAsync(id, Ids.Parse(ctx.Request.RouteValues["msid"]?.ToString())) is not { } item || item.IsFolder) return Results.NotFound();
        var index = int.Parse(ctx.Request.RouteValues["index"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
        var format = Path.GetExtension(ctx.Request.RouteValues["file"]?.ToString() ?? "").TrimStart('.').ToLowerInvariant();
        var track = SubtitleTrack.Find(item, index);
        if (track is { Embedded: not null, Codec.Length: 0 })
        {
            // Known only from the NFO: the codec decides whether (and how) it can be extracted.
            await probe.EnsureAsync(item, ctx.RequestAborted);
            track = SubtitleTrack.Find(item, index);
        }
        if (track is null) return Results.NotFound();
        if (track.File is null) return await EmbeddedSubtitle(ctx, item, track, format, extractor);

        var sub = track.File;
        if (!File.Exists(sub.Path)) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "public, max-age=3600";
        if (sub.Format is "ass" or "ssa" && format is not ("vtt" or "js"))
            return Results.File(sub.Path, "text/x-ssa");
        var text = Subtitles.ReadText(sub.Path);
        return format switch
        {
            "js" or "json" => Results.Json(Subtitles.ToTrackEvents(text), AppJson.Default.SubtitleTrackEvents),
            "srt" or "subrip" when sub.Format == "srt" => Results.Text(text, "application/x-subrip"),
            _ => Results.Text(Subtitles.ToVtt(text), "text/vtt"),
        };
    }

    private static async Task<IResult> EmbeddedSubtitle(HttpContext ctx, Item item, SubtitleTrack track, string format, SubtitleExtractor extractor)
    {
        try
        {
            if (format is "js" or "json")
            {
                // TrackEvents for jellyfin-web's custom renderer, from the SubRip form.
                if (await extractor.GetAsync(item, track, "srt", ctx.RequestAborted) is not { } srt) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "public, max-age=3600";
                return Results.Json(Subtitles.ToTrackEvents(Subtitles.ReadText(srt)), AppJson.Default.SubtitleTrackEvents);
            }
            if (await extractor.GetAsync(item, track, format, ctx.RequestAborted) is not { } path) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            var contentType = Path.GetExtension(path) switch
            {
                ".vtt" => "text/vtt",
                ".srt" => "application/x-subrip",
                ".ass" or ".ssa" => "text/x-ssa",
                ".mks" => "video/x-matroska",
                _ => "application/octet-stream",
            };
            return Results.File(path, contentType, enableRangeProcessing: true);
        }
        catch (SubtitleExtractionException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private enum ReportKind { Start, Progress, Stopped }

    /// <summary>
    /// Persists playback state. Clients report progress every ~10 s, so each report is
    /// one upsert; "played" is decided on stop from the configured threshold.
    /// </summary>
    private static async Task<IResult> Report(HttpContext ctx, IUserDataStore userData, ItemRepository items, SessionRegistry sessions,
        IOptions<ServerOptions> options, ReportKind kind, TranscodeManager? transcodes = null)
    {
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.PlaybackReport);
        if (kind == ReportKind.Stopped && body?.PlaySessionId is { Length: > 0 } ps) transcodes?.StopSession(ps, null);
        if (kind == ReportKind.Progress && body is not null) transcodes?.ReportPosition(body.PlaySessionId, body.PositionTicks, body.IsPaused);
        if (body is null || Ids.Parse(body.ItemId) is not { } itemId) return Results.NoContent();
        var auth = ctx.Auth();
        // Played, resumable and watched belong to the movie or episode, whichever of its files plays.
        if (await items.GetAsync(itemId) is { PrimaryId: { } mainId }) itemId = mainId;
        var state = sessions.Touch(auth);
        state.NowPlayingItemId = kind == ReportKind.Stopped ? null : itemId;
        state.PositionTicks = body.PositionTicks;
        state.IsPaused = body.IsPaused;
        state.PlayMethod = body.PlayMethod;

        if (await items.GetAsync(itemId) is not { } item) return Results.NoContent();
        var position = body.PositionTicks ?? 0;
        var playback = options.Value.Playback;
        switch (kind)
        {
            case ReportKind.Progress when position > 0:
                await userData.SaveProgressAsync(auth.User.Id, itemId, position);
                break;
            case ReportKind.Stopped:
                if (item.RuntimeTicks > 0 && position >= item.RuntimeTicks * playback.PlayedThreshold)
                    await userData.MarkPlayedAsync(auth.User.Id, itemId, true);
                else if (item.RuntimeTicks > 0 && position < item.RuntimeTicks * playback.MinResumeFraction)
                    await userData.SaveProgressAsync(auth.User.Id, itemId, 0, durable: true);
                else if (position > 0)
                    await userData.SaveProgressAsync(auth.User.Id, itemId, position, durable: true);
                break;
        }
        return Results.NoContent();
    }

    private static async Task<IResult> Sessions(HttpContext ctx, SessionRegistry sessions, ItemRepository items, DtoBuilder dto, ServerIdentity identity)
    {
        var active = sessions.Active(TimeSpan.FromMinutes(10)).ToList();
        var me = ctx.Auth().User;
        if (!me.IsAdmin) active = active.Where(s => s.UserId == me.Id).ToList();
        var playing = await items.GetManyAsync(active.Select(s => s.NowPlayingItemId).OfType<Guid>().Distinct().ToList());
        var playingDtos = (await dto.BuildAsync(playing, null, FieldSet.Parse(ctx.Request))).ToDictionary(d => d.Id);
        return Results.Json(active.Select(s => new SessionInfoDto
        {
            Id = s.Id, UserId = s.UserId.N(), UserName = s.UserName, Client = s.Client, DeviceName = s.DeviceName,
            DeviceId = s.DeviceId, ApplicationVersion = s.Version, LastActivityDate = s.LastActivity, ServerId = identity.ServerId,
            PlayState = new PlayStateDto { PositionTicks = s.PositionTicks, IsPaused = s.IsPaused, PlayMethod = s.PlayMethod },
            NowPlayingItem = s.NowPlayingItemId is { } nid ? playingDtos.GetValueOrDefault(nid.N()) : null,
        }).ToList(), AppJson.Default.ListSessionInfoDto);
    }

    /// <summary>
    /// Minimal Jellyfin WebSocket: announces the keep-alive interval and answers KeepAlive.
    /// Enough for jellyfin-web to consider the server "connected".
    /// </summary>
    private static async Task Socket(HttpContext ctx, AuthService auth, ILogger<WebSocketMessage> log)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (await auth.ResolveAsync(ctx) is not { } info)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var ct = ctx.RequestAborted;
        async Task Send(string type, JsonElement? data = null)
        {
            var msg = JsonSerializer.SerializeToUtf8Bytes(new WebSocketMessage { MessageType = type, MessageId = Guid.NewGuid().ToString("N"), Data = data }, AppJson.Default.WebSocketMessage);
            await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
        }
        try
        {
            using (var interval = JsonDocument.Parse("60")) await Send("ForceKeepAlive", interval.RootElement.Clone());
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (!result.EndOfMessage) continue;
                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (text.Contains("\"KeepAlive\"", StringComparison.Ordinal)) await Send("KeepAlive");
            }
            if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            log.LogDebug("WebSocket closed for {User}: {Error}", info.User.Name, ex.Message);
        }
    }
}
