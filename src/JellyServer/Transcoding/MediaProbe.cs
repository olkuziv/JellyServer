using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JellyServer.Configuration;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using Microsoft.Extensions.Options;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Transcoding;

/// <summary>
/// Reads real stream info with ffprobe, lazily: only when a client needs a playback
/// decision and the NFO did not provide codecs. Results are stored on the item, so each
/// file is probed once (until it changes and the scanner resets it).
/// </summary>
public sealed class MediaProbe(IOptions<ServerOptions> options, ItemRepository items, ILogger<MediaProbe> log)
{
    private readonly TranscodingOptions _opt = options.Value.Transcoding;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<MediaInfo?>>> _inflight = new();

    /// <summary>Returns usable stream info, probing the file when codecs are unknown.</summary>
    public async Task<MediaInfo?> EnsureAsync(Item item, CancellationToken ct)
    {
        if (HasCodecs(item.MediaInfo) || item.IsStrm || !_opt.Enabled) return item.MediaInfo;
        var lazy = _inflight.GetOrAdd(item.Id, _ => new Lazy<Task<MediaInfo?>>(() => ProbeAndSaveAsync(item)));
        try { return await lazy.Value.WaitAsync(ct) ?? item.MediaInfo; }
        finally { _inflight.TryRemove(item.Id, out _); }
    }

    // Embedded subtitles known only by language (NFO, or probed before stream details were stored) need a
    // probe too: their codec decides text extraction vs image delivery.
    private static bool HasCodecs(MediaInfo? mi) =>
        mi is not null && mi.Video.Count > 0 && mi.Video.All(v => v.Codec.Length > 0) && mi.Audio.All(a => a.Codec.Length > 0)
        && (mi.Subtitles.Count == 0 || mi.SubtitleStreams is not null);

    private async Task<MediaInfo?> ProbeAndSaveAsync(Item item)
    {
        var sw = Stopwatch.StartNew();
        var mi = await ProbeAsync(item.Path);
        if (mi is null) return null;
        var runtime = mi.DurationSec > 0 ? (long)(mi.DurationSec * TimeSpan.TicksPerSecond) : 0;
        await items.SaveMediaInfoAsync(item.Id, mi, runtime, item.PrimaryId);
        item.MediaInfo = mi;
        if (item.RuntimeTicks == 0) item.RuntimeTicks = runtime;
        log.LogInformation("Probed {Path} in {Ms} ms: video={Video} audio={Audio}", item.Path, sw.ElapsedMilliseconds,
            string.Join(',', mi.Video.Select(v => $"{v.Codec} {v.Width}x{v.Height}")), string.Join(',', mi.Audio.Select(a => $"{a.Codec}/{a.Channels}ch/{a.Language}")));
        return mi;
    }

    public async Task<MediaInfo?> ProbeAsync(string path)
    {
        var psi = new ProcessStartInfo(_opt.FfprobePath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            if (proc.ExitCode != 0)
            {
                log.LogWarning("ffprobe failed for {Path}: {Error}", path, (await stderr).Trim());
                return null;
            }
            return Parse(await stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        {
            log.LogWarning("ffprobe unavailable or timed out for {Path}: {Error}", path, ex.Message);
            return null;
        }
    }

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var mi = new MediaInfo { Probed = true };
        if (root.TryGetProperty("format", out var format))
        {
            mi.DurationSec = Num(format, "duration");
            mi.Bitrate = (long)Num(format, "bit_rate");
        }
        if (!root.TryGetProperty("streams", out var streams)) return mi;
        foreach (var s in streams.EnumerateArray())
        {
            var type = Str(s, "codec_type");
            var codec = Codecs.NormalizeVideo(Str(s, "codec_name") ?? "");
            var lang = s.TryGetProperty("tags", out var tags) ? Str(tags, "language") : null;
            switch (type)
            {
                case "video" when !IsAttachedPicture(s):
                    var transfer = Str(s, "color_transfer");
                    var hdr = transfer switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => null };
                    var bitDepth = int.TryParse(Str(s, "bits_per_raw_sample"), out var bd) ? bd
                        : (Str(s, "pix_fmt") ?? "").Contains("10") ? 10 : 8;
                    mi.Video.Add(new VideoStream(codec, Int(s, "width"), Int(s, "height"), 0, hdr)
                    {
                        Profile = Str(s, "profile"), BitDepth = bitDepth,
                        FrameRate = Rate(Str(s, "avg_frame_rate")) is > 0 and var fps ? fps : Rate(Str(s, "r_frame_rate")),
                        // tt/bb/tb/bt: interlaced (or telecined) fields; "progressive" and "unknown" are left alone.
                        Interlaced = Str(s, "field_order") is "tt" or "bb" or "tb" or "bt",
                    });
                    break;
                case "audio":
                    mi.Audio.Add(new AudioStream(Codecs.NormalizeAudio(Str(s, "codec_name") ?? "", Str(s, "profile")), lang, Int(s, "channels")));
                    break;
                case "subtitle":
                    mi.Subtitles.Add(lang ?? "");
                    (mi.SubtitleStreams ??= []).Add(new EmbeddedSubtitle(Int(s, "index"), SubtitleCodecs.Normalize(Str(s, "codec_name") ?? ""),
                        lang is { Length: > 0 } ? lang : null)
                    {
                        Title = tags.ValueKind == JsonValueKind.Object && Str(tags, "title") is { Length: > 0 } title ? title : null,
                        IsDefault = Disposition(s, "default"), IsForced = Disposition(s, "forced"),
                        IsHearingImpaired = Disposition(s, "hearing_impaired"),
                    });
                    break;
            }
        }
        return mi;
    }

    private static bool IsAttachedPicture(JsonElement s) => Disposition(s, "attached_pic");

    private static bool Disposition(JsonElement s, string name) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) && i == 1;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    /// <summary>ffprobe rates are fractions such as "24000/1001"; "0/0" means unknown.</summary>
    private static double Rate(string? fraction)
    {
        if (fraction?.Split('/') is not [var n, var d]) return 0;
        return double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0 ? Math.Round(num / den, 3) : 0;
    }

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>Maps the many spellings of codec names (NFO, ffprobe, client profiles) to one form.</summary>
public static class Codecs
{
    public static string NormalizeVideo(string c) => c.Trim().ToLowerInvariant() switch
    {
        "avc" or "avc1" or "h.264" or "x264" or "h264" => "h264",
        "h265" or "h.265" or "x265" or "hvc1" or "hev1" or "hevc" => "hevc",
        "xvid" or "divx" or "dx50" or "mpeg4" => "mpeg4",
        "vc-1" or "wvc1" or "vc1" => "vc1",
        "mpeg2video" or "mpeg2" => "mpeg2video",
        var x => x,
    };

    public static string NormalizeAudio(string c, string? profile = null) => c.Trim().ToLowerInvariant() switch
    {
        "dca" or "dts" or "dts-hd" or "dtshd" or "dts-hd ma" or "dts-ma" => "dts",
        "e-ac3" or "ec-3" or "eac-3" or "eac3" => "eac3",
        "ac-3" or "ac3" => "ac3",
        "mp3float" or "mp3" => "mp3",
        "truehd" or "mlp" => "truehd",
        var x => x,
    };

    /// <summary>Splits a profile codec list; an empty list means "anything".</summary>
    public static HashSet<string> List(string? csv, bool video) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => video ? NormalizeVideo(c) : NormalizeAudio(c))
            .ToHashSet();

    /// <summary>Whether our container name satisfies a profile's container list.</summary>
    public static bool ContainerMatches(string? container, string? profileContainers)
    {
        if (string.IsNullOrEmpty(profileContainers)) return true;
        var aliases = (container ?? "").ToLowerInvariant() switch
        {
            "mp4" or "m4v" or "mov" => new[] { "mp4", "m4v", "mov" },
            "mkv" => ["mkv", "matroska"],
            "ts" => ["ts", "mpegts", "m2ts"],
            var x => [x],
        };
        var allowed = profileContainers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(a => aliases.Contains(a, StringComparer.OrdinalIgnoreCase));
    }
}
