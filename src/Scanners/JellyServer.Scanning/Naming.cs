using System.Text;
using System.Text.RegularExpressions;

namespace JellyServer.Scanning;

/// <summary>Filename/folder-name heuristics for movies, seasons and episodes.</summary>
public static partial class Naming
{
    public static readonly Dictionary<string, string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = "mkv", [".mp4"] = "mp4", [".m4v"] = "mp4", [".avi"] = "avi", [".ts"] = "ts",
        [".m2ts"] = "ts", [".mts"] = "ts", [".mov"] = "mov", [".wmv"] = "wmv", [".webm"] = "webm",
        [".mpg"] = "mpeg", [".mpeg"] = "mpeg", [".flv"] = "flv", [".3gp"] = "3gp", [".ogv"] = "ogg",
        [".vob"] = "mpeg", [".iso"] = "iso", [".strm"] = "strm",
    };

    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".tbn"];

    public static readonly Dictionary<string, string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".srt"] = "srt", [".vtt"] = "vtt", [".ass"] = "ass", [".ssa"] = "ssa",
    };

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts",
        "trailers", "sample", "samples", "other", "@eadir", "#recycle", "$recycle.bin",
        "system volume information", ".actors", "metadata", "subs", "subtitles",
    };

    public static bool IsIgnoredDir(string name) => name.StartsWith('.') || IgnoredDirs.Contains(name);

    public static bool IsSampleFile(string baseName)
    {
        var l = baseName.ToLowerInvariant();
        return l == "sample" || l.EndsWith("-sample") || l.EndsWith(".sample") || l.EndsWith("-trailer");
    }

    [GeneratedRegex(@"[\(\[]((?:19|20)\d{2})[\)\]]")] private static partial Regex YearParen();
    [GeneratedRegex(@"(?:^|[ ._])((?:19|20)\d{2})(?:[ ._]|$)")] private static partial Regex YearBare();
    // "[01] Title", "(2) Title" or "01 - Title" / "1. Title": a collection index. A bare number is part of the title ("12 Angry Men", "300").
    [GeneratedRegex(@"^\s*(?:[\[\(]\d{1,3}[\]\)][ ._-]*|\d{1,3}[ ]*[._-]+[ ]*)")] private static partial Regex LeadingIndex();
    [GeneratedRegex(@"\[[^\]]*\]|\{[^\}]*\}")] private static partial Regex Brackets();
    [GeneratedRegex(@"\b(2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr10?|dv|bdrip|brrip|bluray|blu-ray|web-?dl|web-?rip|webrip|hdtv|hdrip|dvdrip|remux|x264|x265|h\.?264|h\.?265|hevc|avc|aac|ac3|dts|ddp?5\.1|10bit|imax|extended|unrated|proper|repack)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseJunk();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex MultiSpace();

    /// <summary>"[01] Pacific Rim (2013) BDRip 1080p [UKR_ENG]" → ("Pacific Rim", 2013).</summary>
    public static (string Title, int? Year) ParseMovieName(string name)
    {
        int? year = null;
        var cut = name.Length;
        var m = YearParen().Match(name);
        if (m.Success)
        {
            year = int.Parse(m.Groups[1].Value);
            cut = m.Index;
        }
        else if ((m = YearBare().Match(name)).Success && m.Groups[1].Index > 0)
        {
            year = int.Parse(m.Groups[1].Value);
            cut = m.Groups[1].Index;
        }
        var title = name[..cut];
        var junk = ReleaseJunk().Match(title);
        if (junk.Success && junk.Index > 0) title = title[..junk.Index];
        title = LeadingIndex().Replace(title, "");
        title = Brackets().Replace(title, " ");
        if (!title.Contains(' ')) title = title.Replace('.', ' ').Replace('_', ' ');
        title = MultiSpace().Replace(title, " ").Trim(' ', '.', '-', '_');
        return (title.Length == 0 ? name.Trim() : title, year);
    }

    [GeneratedRegex(@"(?:^|[^a-z0-9])s(\d{1,3})[ ._-]*e(\d{1,4})", RegexOptions.IgnoreCase)] private static partial Regex SxxEyy();
    [GeneratedRegex(@"(?:^|[^a-z0-9])(\d{1,2})x(\d{2,3})(?:[^0-9]|$)", RegexOptions.IgnoreCase)] private static partial Regex NxNN();
    [GeneratedRegex(@"(?:^|[^\p{L}])(?:ep|episode|серія|серия|эпизод|епізод)[ ._#-]*(\d{1,4})", RegexOptions.IgnoreCase)] private static partial Regex EpWord();
    [GeneratedRegex(@"\s-\s(\d{1,4})(?:[^0-9]|$)")] private static partial Regex DashNumber();
    [GeneratedRegex(@"^(\d{1,4})(?:[ ._-]|$)")] private static partial Regex LeadingNumber();
    [GeneratedRegex(@"^(?:season|сезон|series|s)[ ._-]*(\d{1,3})", RegexOptions.IgnoreCase)] private static partial Regex SeasonDir();
    [GeneratedRegex(@"(\d{1,3})[ ._-]*(?:season|сезон)", RegexOptions.IgnoreCase)] private static partial Regex SeasonAfter();
    [GeneratedRegex(@"^(\d{1,3})(?:\D.*)?$")] private static partial Regex BareNumberDir();

    [GeneratedRegex(@"(?:^|[\s._\[(-])(?:s|season)[ ._-]*(\d{1,3})(?=$|[\s._\])-])", RegexOptions.IgnoreCase)] private static partial Regex SeasonToken();

    /// <summary>
    /// True when a folder name carries an explicit season marker anywhere ("Season 2", "Show S01", "Show S02 2007-2008");
    /// bare numbers do not count, so "12 Angry Men (1957)" is not a season.
    /// </summary>
    public static bool HasSeasonMarker(string name) =>
        ParseSeasonDir(name, allowBareNumber: false) is not null || SeasonToken().IsMatch(name);

    [GeneratedRegex(@"[\[\{\(]\s*(tmdb|tmdbid|imdb|imdbid|tvdb|tvdbid)\s*[-=:_ ]\s*(tt\d+|\d+)\s*[\]\}\)]", RegexOptions.IgnoreCase)] private static partial Regex ProviderIdTag();

    /// <summary>
    /// Provider ids written into names the way Jellyfin expects: "Alpha (2019) [tmdbid-123]", "{imdb-tt0133093}", "[tvdbid-81189]".
    /// Keys are Tmdb, Imdb and Tvdb; the first name that carries a key wins.
    /// </summary>
    public static Dictionary<string, string> ParseProviderIds(params string?[] names)
    {
        var ids = new Dictionary<string, string>();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            foreach (Match m in ProviderIdTag().Matches(name))
            {
                var key = m.Groups[1].Value.ToLowerInvariant() switch { "tmdb" or "tmdbid" => "Tmdb", "imdb" or "imdbid" => "Imdb", _ => "Tvdb" };
                ids.TryAdd(key, m.Groups[2].Value);
            }
        }
        return ids;
    }

    public readonly record struct EpisodeInfo(int? Season, int? Episode, string Rest);

    /// <summary>Recognises S01E02, S01 E02, 1x02, "Ep 02", "Name - 02" and "02 Title".</summary>
    public static EpisodeInfo ParseEpisode(string baseName)
    {
        var m = SxxEyy().Match(baseName);
        if (m.Success)
            return new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), baseName[(m.Index + m.Length)..]);
        if ((m = NxNN().Match(baseName)).Success)
            return new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), baseName[(m.Groups[2].Index + m.Groups[2].Length)..]);
        foreach (var re in new[] { EpWord(), DashNumber(), LeadingNumber() })
        {
            m = re.Match(baseName);
            if (m.Success)
                return new(null, int.Parse(m.Groups[1].Value), baseName[(m.Groups[1].Index + m.Groups[1].Length)..]);
        }
        return new(null, null, "");
    }

    /// <summary>"Season 1 - 1080p", "Season 01", "S2", "2 сезон", "3", "Specials" → number; otherwise null.</summary>
    public static int? ParseSeasonDir(string name, bool allowBareNumber = true)
    {
        var l = name.Trim().ToLowerInvariant();
        if (l is "specials" or "special") return 0;
        foreach (var re in allowBareNumber ? new[] { SeasonDir(), SeasonAfter(), BareNumberDir() } : [SeasonDir(), SeasonAfter()])
        {
            var m = re.Match(l);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    /// <summary>Text after the episode marker as a title, or null when it is release noise ("(2021) WEBRip", "- hdvb").</summary>
    public static string? EpisodeTitle(string rest)
    {
        var t = rest.Trim(' ', '.', '_', '-', '–');
        if (t.Length == 0 || t.StartsWith('(') || t.StartsWith('[') || ReleaseJunk().IsMatch(t)) return null;
        t = MultiSpace().Replace(Brackets().Replace(t, " "), " ").Trim();
        if (t.Length < 6 && t == t.ToLowerInvariant() && t.All(char.IsAscii)) return null; // source tags like "hdvb"
        return t.Length == 0 ? null : t;
    }

    /// <summary>Maps a file (or a .strm target URL) to a Jellyfin container name.</summary>
    public static string? ContainerFor(string path, string? streamUrl)
    {
        if (!string.IsNullOrEmpty(streamUrl))
        {
            var u = streamUrl.ToLowerInvariant();
            var q = u.IndexOfAny(['?', '#']);
            if (q >= 0) u = u[..q];
            if (u.EndsWith(".m3u8") || u.Contains("/hls/")) return "hls";
            return VideoExtensions.TryGetValue(System.IO.Path.GetExtension(u), out var c) && c != "strm" ? c : "hls";
        }
        return VideoExtensions.GetValueOrDefault(System.IO.Path.GetExtension(path));
    }
}
