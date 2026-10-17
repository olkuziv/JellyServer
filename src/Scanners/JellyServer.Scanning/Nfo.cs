using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Scanning;

/// <summary>Lenient reader for Kodi/Jellyfin &lt;movie&gt;, &lt;tvshow&gt; and &lt;episodedetails&gt; NFO files.</summary>
public sealed partial class Nfo
{
    private const int MaxSize = 4 << 20;
    private readonly XElement _root;

    private Nfo(XElement root) => _root = root;

    [GeneratedRegex(@"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)")]
    private static partial Regex BareAmpersand();

    /// <summary>Parses an NFO; returns null for empty, URL-only or hopelessly broken files.</summary>
    public static Nfo? Read(string path, ILogger log)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxSize) return null;
            text = File.ReadAllText(path); // detects BOM/encoding
        }
        catch (IOException ex)
        {
            log.LogWarning("Cannot read NFO {Path}: {Error}", path, ex.Message);
            return null;
        }
        var start = text.IndexOf('<');
        if (start < 0) return null; // URL-only NFO
        text = text[start..];
        var end = text.LastIndexOf('>');
        if (end > 0) text = text[..(end + 1)];

        foreach (var attempt in new[] { text, BareAmpersand().Replace(text, "&amp;") })
        {
            try
            {
                var doc = XDocument.Parse(attempt, LoadOptions.None);
                return doc.Root is null ? null : new Nfo(doc.Root);
            }
            catch (XmlException ex) when (attempt != text)
            {
                log.LogDebug("Unparseable NFO {Path}: {Error}", path, ex.Message);
            }
            catch (XmlException)
            {
                // retry with sanitised ampersands
            }
        }
        return null;
    }

    private string? Str(string name)
    {
        var v = _root.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private IEnumerable<string> All(string name) =>
        _root.Elements(name).Select(e => e.Value.Trim()).Where(v => v.Length > 0);

    public string? Title => Str("title");
    public string? EpisodeNumber => Str("episode");

    /// <summary>Copies NFO metadata onto an item; filename-derived values stay as fallbacks.</summary>
    public void ApplyTo(Item it)
    {
        if (Str("title") is { } title)
        {
            it.Name = title;
            it.NameLocked = true;
        }
        if (Str("originaltitle") is { } ot && ot != it.Name) it.OriginalTitle = ot;
        it.SortName = ItemNaming.IsValidSortTitle(it.Name, Str("sorttitle"))
            ? ItemNaming.SortName(Str("sorttitle")!)
            : ItemNaming.SortName(it.Name);
        it.Overview = Str("plot") ?? Str("outline") ?? it.Overview;
        it.Tagline = Str("tagline");
        if (int.TryParse(Str("year"), out var year) && year > 1800) it.ProductionYear = year;
        foreach (var d in new[] { "premiered", "aired", "releasedate" })
        {
            if (ParseDate(Str(d)) is { } date)
            {
                it.PremiereDate = date;
                it.ProductionYear ??= date.Year;
                break;
            }
        }
        it.EndDate = ParseDate(Str("enddate"));
        if (ParseDate(Str("dateadded")) is { } added) it.DateCreated = added;
        if (CommunityRating() is { } rating) it.CommunityRating = rating;
        if (ParseFloat(Str("criticrating")) is { } critic and > 0) it.CriticRating = critic;
        it.OfficialRating = Str("mpaa");
        if (ParseDouble(Str("runtime")) is { } minutes and > 0) it.RuntimeTicks = (long)(minutes * 60 * TimeSpan.TicksPerSecond);
        it.Genres = SplitDistinct(All("genre"));
        it.Studios = SplitDistinct(All("studio"));
        var set = _root.Element("set");
        it.CollectionName = set?.Element("name")?.Value.Trim() is { Length: > 0 } sn ? sn
            : set?.Value.Trim() is { Length: > 0 } st ? st : null;
        foreach (var (key, value) in ProviderIds()) it.ProviderIds[key] = value; // the NFO wins over ids from the name
        it.People = People();
        if (MediaInfo() is { } mi)
        {
            it.MediaInfo = mi;
            if (it.RuntimeTicks == 0 && mi.DurationSec > 0) it.RuntimeTicks = (long)(mi.DurationSec * TimeSpan.TicksPerSecond);
        }
    }

    private float? CommunityRating()
    {
        var ratings = _root.Element("ratings")?.Elements("rating").ToList() ?? [];
        var preferred = ratings.FirstOrDefault(r => (string?)r.Attribute("default") == "true") ?? ratings.FirstOrDefault();
        return ParseFloat(Str("rating")) is { } r and > 0 ? r
            : ParseFloat(preferred?.Element("value")?.Value) is { } v and > 0 ? v : null;
    }

    private Dictionary<string, string> ProviderIds()
    {
        var ids = new Dictionary<string, string>();
        void Set(string key, string? value)
        {
            value = value?.Trim();
            if (!string.IsNullOrEmpty(value) && value != "0") ids.TryAdd(key, value);
        }
        foreach (var e in _root.Elements("uniqueid").Concat(_root.Elements("id")))
        {
            var provider = ((string?)e.Attribute("type") ?? (string?)e.Attribute("provider") ?? "").ToLowerInvariant();
            var value = e.Value.Trim();
            switch (provider)
            {
                case "tmdb": Set("Tmdb", value); break;
                case "imdb": Set("Imdb", value); break;
                case "tvdb": Set("Tvdb", value); break;
                case "" when value.StartsWith("tt"): Set("Imdb", value); break;
            }
        }
        Set("Tmdb", Str("tmdbid"));
        Set("Imdb", Str("imdbid"));
        Set("Imdb", Str("imdb_id"));
        Set("Tvdb", Str("tvdbid"));
        return ids;
    }

    private List<Person>? People()
    {
        var people = new List<Person>();
        var seen = new HashSet<string>();
        void Add(string? name, string? role, string type)
        {
            name = name?.Trim();
            if (!string.IsNullOrEmpty(name) && seen.Add(type + "|" + name))
                people.Add(new Person(name, string.IsNullOrWhiteSpace(role) ? null : role.Trim(), type));
        }
        foreach (var d in All("director")) Add(d, null, "Director");
        foreach (var w in All("writer")) Add(w, null, "Writer");
        // Cap the cast: nobody scrolls past 50 and it keeps item payloads small.
        foreach (var a in _root.Elements("actor").Take(50))
            Add(a.Element("name")?.Value, a.Element("role")?.Value, "Actor");
        return people.Count > 0 ? people : null;
    }

    private MediaInfo? MediaInfo()
    {
        var sd = _root.Element("fileinfo")?.Element("streamdetails");
        if (sd is null) return null;
        var mi = new MediaInfo();
        foreach (var v in sd.Elements("video"))
        {
            mi.Video.Add(new VideoStream(
                (v.Element("codec")?.Value.Trim() ?? "").ToLowerInvariant(),
                ParseInt(v.Element("width")?.Value), ParseInt(v.Element("height")?.Value),
                ParseFloat(v.Element("aspect")?.Value) ?? 0,
                v.Element("hdrtype")?.Value.Trim() is { Length: > 0 } hdr ? hdr : null)
            {
                FrameRate = ParseDouble(v.Element("framerate")?.Value) ?? 0,
                Interlaced = string.Equals(v.Element("scantype")?.Value.Trim(), "interlaced", StringComparison.OrdinalIgnoreCase),
            });
            if (ParseDouble(v.Element("durationinseconds")?.Value) is { } d && d > mi.DurationSec) mi.DurationSec = d;
        }
        foreach (var a in sd.Elements("audio"))
        {
            mi.Audio.Add(new AudioStream(
                (a.Element("codec")?.Value.Trim() ?? "").ToLowerInvariant(),
                a.Element("language")?.Value.Trim() is { Length: > 0 } l ? l : null,
                ParseInt(a.Element("channels")?.Value)));
        }
        foreach (var s in sd.Elements("subtitle"))
            mi.Subtitles.Add(s.Element("language")?.Value.Trim() ?? "");
        return mi.Video.Count + mi.Audio.Count > 0 ? mi : null;
    }

    /// <summary>Artwork referenced inside the NFO (remote URLs or local paths).</summary>
    public (string? Poster, string? Fanart, string? Landscape, string? Logo) ImageRefs()
    {
        string? poster = null, landscape = null, logo = null;
        foreach (var t in _root.Elements("thumb"))
        {
            var url = t.Value.Trim();
            if (url.Length == 0) continue;
            switch (((string?)t.Attribute("aspect") ?? "").ToLowerInvariant())
            {
                case "" or "poster": poster ??= url; break;
                case "landscape" or "thumb": landscape ??= url; break;
                case "clearlogo" or "logo": logo ??= url; break;
            }
        }
        var fanart = _root.Element("fanart")?.Elements("thumb").Select(t => t.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        var art = _root.Element("art");
        poster ??= art?.Element("poster")?.Value.Trim();
        fanart ??= art?.Element("fanart")?.Value.Trim();
        landscape ??= art?.Element("thumb")?.Value.Trim();
        logo ??= art?.Element("clearlogo")?.Value.Trim();
        return (poster, fanart, landscape, logo);
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd", "dd.MM.yyyy"];

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1800
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
    }

    private static float? ParseFloat(string? s) =>
        float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;

    private static double? ParseDouble(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int ParseInt(string? s) => int.TryParse(s?.Trim(), out var i) ? i : 0;

    /// <summary>Distinct non-empty values; also splits packed values like "Drama / Comedy".</summary>
    private static string[] SplitDistinct(IEnumerable<string> values) =>
        values.SelectMany(v => v.Split(" / ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToArray();
}
