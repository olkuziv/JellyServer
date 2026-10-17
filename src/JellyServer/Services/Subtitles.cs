using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JellyServer.Services;

public sealed record SubtitleTrackEvent(string Id, string Text, long StartPositionTicks, long EndPositionTicks);
public sealed record SubtitleTrackEvents(List<SubtitleTrackEvent> TrackEvents);

/// <summary>
/// Converts sidecar SRT/VTT files to what jellyfin-web asks for: WebVTT (Stream.vtt)
/// or TrackEvents JSON (Stream.js, used by its custom subtitle renderer on TVs).
/// </summary>
public static partial class Subtitles
{
    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    [GeneratedRegex(@"(\d{1,2}:)?(\d{1,2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}:)?(\d{1,2}):(\d{2})[,.](\d{1,3})")]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"</?(?!i>|b>|u>|/i>|/b>|/u>)[^>]+>")]
    private static partial Regex Tags();

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            // Legacy Cyrillic subtitles are commonly Windows-1251.
            return CodePagesEncoding.Value.GetString(bytes);
        }
    }

    private static readonly Lazy<Encoding> CodePagesEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    });

    private static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = TimingLine().Match(lines[i]);
            if (!m.Success) continue;
            var start = Time(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            var end = Time(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
            var sb = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i].TrimEnd());
            }
            cues.Add(new Cue(start, end, sb.ToString()));
        }
        return cues;
    }

    private static TimeSpan Time(string h, string m, string s, string ms) => new(0,
        h.Length > 0 ? int.Parse(h.TrimEnd(':')) : 0, int.Parse(m), int.Parse(s), int.Parse(ms.PadRight(3, '0')));

    public static string ToVtt(string text)
    {
        if (text.StartsWith("WEBVTT")) return text;
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in Parse(text))
            sb.Append(Fmt(c.Start)).Append(" --> ").Append(Fmt(c.End)).Append('\n').Append(c.Text).Append("\n\n");
        return sb.ToString();
    }

    public static SubtitleTrackEvents ToTrackEvents(string text) => new(Parse(text)
        .Select((c, i) => new SubtitleTrackEvent((i + 1).ToString(CultureInfo.InvariantCulture),
            Tags().Replace(c.Text, "").Replace("\n", "<br>"), c.Start.Ticks, c.End.Ticks))
        .ToList());

    private static string Fmt(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
