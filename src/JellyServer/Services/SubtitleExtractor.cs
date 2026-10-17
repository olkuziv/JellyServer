using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using JellyServer.Configuration;
using Microsoft.Extensions.Options;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Services;

public sealed class SubtitleExtractionException(string message) : Exception(message);

/// <summary>
/// Jellyfin's SubtitleEncoder for embedded streams: the first request for any subtitle of a file extracts every
/// extractable stream in ONE ffmpeg pass (the whole file is read once): ASS/SSA/SubRip/PGS/VobSub are copied,
/// other text codecs (webvtt, mov_text…) become SubRip. Other formats are then converted from that small file.
/// Files live in {DataDir}/subtitles/{item}/s{n}-{mtime}-{size}.{ext}, so a changed source gets new names.
/// Concurrent requests share one ffmpeg run; it is not cancelled when a client goes away, and is killed after
/// SubtitleExtractionTimeoutMinutes.
/// </summary>
public sealed class SubtitleExtractor(IOptionsMonitor<ServerOptions> options, ILogger<SubtitleExtractor> log)
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _jobs = new();

    /// <summary>Runs ffmpeg with the given arguments and timeout; returns the exit code (-1 on timeout) and stderr.</summary>
    public Func<IReadOnlyList<string>, TimeSpan, Task<(int ExitCode, string Error)>>? Runner { get; set; }

    private Func<IReadOnlyList<string>, TimeSpan, Task<(int ExitCode, string Error)>> Run => Runner ?? RunFfmpegAsync;

    /// <summary>Whether the stream can be extracted to a file (text with a known codec, PGS, VobSub).</summary>
    public static bool IsExtractable(SubtitleTrack t) => t.Embedded is not null && (t.IsText || t.IsPgs || t.IsVobSub);

    /// <summary>File type the single extraction pass writes for a codec (Jellyfin's GetExtractableSubtitleFileExtension).</summary>
    public static string NativeExtension(string codec) => codec switch
    {
        "ass" or "ssa" => codec,
        _ when SubtitleCodecs.IsPgs(codec) => "sup",
        _ when SubtitleCodecs.IsVobSub(codec) => "mks",
        _ => "srt",
    };

    private static bool IsCopyable(string codec) =>
        codec is "ass" or "ssa" or "srt" or "subrip" || SubtitleCodecs.IsPgs(codec) || SubtitleCodecs.IsVobSub(codec);

    /// <summary>Requested format to a file extension: srt|vtt|ass|ssa|sup|mks (null = unsupported).</summary>
    public static string? FormatExtension(string format) => format.ToLowerInvariant() switch
    {
        "srt" or "subrip" => "srt",
        "vtt" or "webvtt" => "vtt",
        "ass" => "ass",
        "ssa" => "ssa",
        "sup" or "pgs" or "pgssub" => "sup",
        "mks" or "vobsub" or "dvdsub" => "mks",
        _ => null,
    };

    public static string CacheDir(string dataDir, Guid itemId) => Path.Combine(dataDir, "subtitles", itemId.ToString("N"));

    /// <summary>Source revision: cached files are only valid for the same mtime and size.</summary>
    public static string Stamp(DateTime lastWriteUtc, long length) =>
        string.Create(CultureInfo.InvariantCulture, $"{lastWriteUtc.Ticks:x}-{length:x}");

    public static string CachePath(string dir, int ordinal, string stamp, string ext) =>
        Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"s{ordinal}-{stamp}.{ext}"));

    /// <summary>One ffmpeg pass writing every listed stream (Jellyfin's ExtractAllExtractableSubtitles).</summary>
    public static List<string> ExtractArgs(string input, IEnumerable<(SubtitleTrack Track, string Output)> streams)
    {
        List<string> args = ["-nostdin", "-y", "-i", input];
        foreach (var (t, output) in streams)
        {
            // ffprobe's absolute index when known, else the n-th subtitle stream.
            var map = t.Embedded!.Index >= 0 ? $"0:{t.Embedded.Index.ToString(CultureInfo.InvariantCulture)}"
                : $"0:s:{t.Ordinal.ToString(CultureInfo.InvariantCulture)}";
            args.AddRange(["-map", map, "-an", "-vn", "-c:s", IsCopyable(t.Codec) ? "copy" : "srt"]);
            // ffmpeg has no .idx/.sub muxer: VobSub goes into a Matroska subtitle file.
            if (SubtitleCodecs.IsVobSub(t.Codec)) args.AddRange(["-f", "matroska"]);
            args.AddRange(["-flush_packets", "1", output]);
        }
        return args;
    }

    /// <summary>Text conversion of an extracted file (srt/ass/ssa/vtt to srt/ass/ssa/vtt).</summary>
    public static List<string> ConvertArgs(string input, string ext, string output) =>
        ["-nostdin", "-y", "-i", input, "-map", "0:s:0", "-c:s", ext == "vtt" ? "webvtt" : ext, output];

    /// <summary>
    /// Path of the subtitle as <paramref name="format"/>, extracting/converting when needed. Null when the stream cannot
    /// be served in that format (image subtitle as text, unknown codec, missing file).
    /// </summary>
    public async Task<string?> GetAsync(Item item, SubtitleTrack track, string format, CancellationToken ct)
    {
        if (!IsExtractable(track) || item.IsStrm || FormatExtension(format) is not { } want) return null;
        var source = new FileInfo(item.Path);
        if (!source.Exists) return null;

        var native = NativeExtension(track.Codec);
        var isImage = native is "sup" or "mks";
        if (isImage != (want is "sup" or "mks") || (isImage && want != native)) return null;

        var o = options.CurrentValue;
        var dir = CacheDir(o.DataDir, item.Id);
        var stamp = Stamp(source.LastWriteTimeUtc, source.Length);
        var nativePath = CachePath(dir, track.Ordinal, stamp, native);
        if (!File.Exists(nativePath))
        {
            var tracks = SubtitleTrack.List(item).Where(IsExtractable).ToList();
            await Once(dir + "|" + stamp, () => ExtractAllAsync(item.Path, dir, stamp, tracks)).WaitAsync(ct);
            if (!File.Exists(nativePath)) throw new SubtitleExtractionException($"ffmpeg did not extract subtitle stream {track.Index} of {item.Path}");
        }
        // Same format, or SSA asked as ASS (ASS is a superset, the styles are kept).
        if (want == native || (native == "ssa" && want == "ass")) return nativePath;

        var converted = CachePath(dir, track.Ordinal, stamp, want);
        if (!File.Exists(converted))
            await Once(converted, () => ConvertAsync(nativePath, want, converted)).WaitAsync(ct);
        return File.Exists(converted) ? converted : throw new SubtitleExtractionException($"ffmpeg could not convert {nativePath} to {want}");
    }

    /// <summary>Runs <paramref name="work"/> once per key; concurrent callers await the same task.</summary>
    private Task Once(string key, Func<Task> work) =>
        _jobs.GetOrAdd(key, k => new Lazy<Task>(async () =>
        {
            try { await work(); }
            finally { _jobs.TryRemove(k, out _); }
        })).Value;

    private async Task ExtractAllAsync(string input, string dir, string stamp, List<SubtitleTrack> tracks)
    {
        var outputs = tracks.Select(t => (Track: t, Final: CachePath(dir, t.Ordinal, stamp, NativeExtension(t.Codec))))
            .Where(x => !File.Exists(x.Final)).ToList();
        if (outputs.Count == 0) return;
        Directory.CreateDirectory(dir);
        // Old revisions of this item are dead weight.
        foreach (var stale in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).Contains(stamp, StringComparison.Ordinal)))
            TryDelete(stale);

        var temps = outputs.Select(x => TempPath(x.Final)).ToList();
        var sw = Stopwatch.StartNew();
        var (exit, error) = await Run(ExtractArgs(input, outputs.Select((x, i) => (x.Track, temps[i]))), Timeout());
        try
        {
            if (exit != 0) throw Failure(input, exit, error);
            for (var i = 0; i < outputs.Count; i++)
            {
                if (!File.Exists(temps[i])) throw Failure(input, exit, error);
                if (outputs[i].Final.EndsWith(".ass", StringComparison.Ordinal) || outputs[i].Final.EndsWith(".ssa", StringComparison.Ordinal))
                    await SetAssFontAsync(temps[i]);
                File.Move(temps[i], outputs[i].Final, overwrite: true);
            }
            log.LogInformation("Extracted {Count} subtitle streams from {Path} in {Ms} ms", outputs.Count, input, sw.ElapsedMilliseconds);
        }
        finally
        {
            foreach (var t in temps) TryDelete(t);
        }
    }

    private async Task ConvertAsync(string input, string ext, string output)
    {
        var temp = TempPath(output);
        try
        {
            var (exit, error) = await Run(ConvertArgs(input, ext, temp), Timeout());
            if (exit != 0 || !File.Exists(temp)) throw Failure(input, exit, error);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private TimeSpan Timeout() => TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.Transcoding.Subtitles.ExtractionTimeoutMinutes));

    private SubtitleExtractionException Failure(string input, int exit, string error)
    {
        log.LogError("ffmpeg subtitle extraction failed for {Path} (exit {Exit}): {Error}", input, exit, error.Trim());
        return new SubtitleExtractionException(exit == -1 ? $"subtitle extraction timed out for {input}" : $"ffmpeg subtitle extraction failed for {input}");
    }

    /// <summary>ffmpeg picks the muxer from the extension, so the temp name keeps it.</summary>
    private static string TempPath(string final) =>
        Path.Combine(Path.GetDirectoryName(final)!, $"{Path.GetFileNameWithoutExtension(final)}.{Guid.NewGuid():N}.part{Path.GetExtension(final)}");

    /// <summary>Jellyfin's SetAssFont: Arial lacks most scripts, "Arial Unicode MS" lets the renderer fall back.</summary>
    private static async Task SetAssFontAsync(string file)
    {
        var text = await File.ReadAllTextAsync(file);
        var fixedText = text.Replace(",Arial,", ",Arial Unicode MS,", StringComparison.Ordinal);
        if (fixedText != text) await File.WriteAllTextAsync(file, fixedText);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<(int ExitCode, string Error)> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(options.CurrentValue.Transcoding.FfmpegPath)
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        log.LogInformation("{Ffmpeg} {Args}", psi.FileName, string.Join(' ', args));
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception ex) { return (-2, ex.Message); }
        using var _ = proc;
        proc.StandardInput.Close();
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return (proc.ExitCode, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "timed out after " + timeout);
        }
    }
}
