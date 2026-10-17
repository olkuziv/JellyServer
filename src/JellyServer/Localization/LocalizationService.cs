using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace JellyServer.Localization;

/// <summary>
/// Static localisation reference data (cultures, countries, parental ratings, UI languages) ported from Jellyfin's
/// LocalizationManager. Everything is embedded, parsed once, and served as pre-serialised UTF-8.
/// </summary>
public sealed class LocalizationService
{
    private const string DataPrefix = "JellyServer.Localization.Data.";
    private const string DefaultCountry = "us";
    private static readonly Assembly Asm = typeof(LocalizationService).Assembly;

    private readonly Lazy<byte[]> _cultures = new(BuildCultures);
    private readonly Lazy<byte[]> _countries = new(() => ReadResource("countries.json"));
    private readonly Lazy<byte[]> _options = new(() => ReadResource("options.json"));
    private readonly Lazy<Dictionary<string, List<RatingEntry>>> _ratings = new(LoadRatings);
    private readonly ConcurrentDictionary<string, byte[]> _ratingsJson = new(StringComparer.OrdinalIgnoreCase);

    public byte[] CulturesJson => _cultures.Value;
    public byte[] CountriesJson => _countries.Value;
    public byte[] OptionsJson => _options.Value;

    /// <summary>Parental ratings for the server's metadata country (falls back to US), with Jellyfin's standard extras.</summary>
    public byte[] ParentalRatingsJson(string? countryCode)
    {
        var key = string.IsNullOrWhiteSpace(countryCode) ? DefaultCountry : countryCode.Trim().ToLowerInvariant();
        return _ratingsJson.GetOrAdd(key, BuildRatings);
    }

    private static byte[] ReadResource(string name)
    {
        using var s = Asm.GetManifestResourceStream(DataPrefix + name) ?? throw new InvalidOperationException($"Missing resource {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] BuildCultures()
    {
        var list = new List<(string Name, string Display, string Two, string[] Three)>();
        using var reader = new StreamReader(new MemoryStream(ReadResource("iso6392.txt")));
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split('|');
            if (p.Length != 5) throw new InvalidDataException($"Invalid culture data found at: '{line}'");
            var display = p[3];
            if (string.IsNullOrWhiteSpace(display)) continue;
            var name = display;
            var two = string.IsNullOrWhiteSpace(p[2]) ? string.Empty : p[2];
            if (two.Contains('-')) name = two;
            // With two codes the first is ISO 639-2/T and the second 639-2/B.
            list.Add((name, display, two, string.IsNullOrWhiteSpace(p[1]) ? [p[0]] : [p[0], p[1]]));
        }

        var distinct = list.DistinctBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Display, StringComparer.InvariantCulture);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var c in distinct)
            {
                w.WriteStartObject();
                w.WriteString("Name", c.Name);
                w.WriteString("DisplayName", c.Display);
                w.WriteString("TwoLetterISOLanguageName", c.Two);
                w.WriteString("ThreeLetterISOLanguageName", c.Three[0]);
                w.WriteStartArray("ThreeLetterISOLanguageNames");
                foreach (var t in c.Three) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return ms.ToArray();
    }

    private sealed record RatingEntry(string Name, int? Score, int? SubScore);

    private static Dictionary<string, List<RatingEntry>> LoadRatings()
    {
        var result = new Dictionary<string, List<RatingEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in Asm.GetManifestResourceNames().Where(n => n.StartsWith(DataPrefix + "Ratings.", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(ReadResource(res[DataPrefix.Length..]));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array) continue;
            var entries = new Dictionary<string, RatingEntry>(StringComparer.Ordinal);
            foreach (var r in ratings.EnumerateArray())
            {
                int? score = null, sub = null;
                if (r.TryGetProperty("ratingScore", out var rs) && rs.ValueKind == JsonValueKind.Object)
                {
                    if (rs.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number) score = s.GetInt32();
                    if (rs.TryGetProperty("subScore", out var ss) && ss.ValueKind == JsonValueKind.Number) sub = ss.GetInt32();
                }
                if (!r.TryGetProperty("ratingStrings", out var strings)) continue;
                foreach (var str in strings.EnumerateArray())
                    entries[str.GetString()!] = new RatingEntry(str.GetString()!, score, score is null ? null : sub);
            }
            result[root.GetProperty("countryCode").GetString()!] = [.. entries.Values];
        }
        return result;
    }

    private byte[] BuildRatings(string country)
    {
        var ratings = _ratings.Value.TryGetValue(country, out var l) ? [.. l] : new List<RatingEntry>();

        // Jellyfin always adds these so clients can pick a limit regardless of the country's own scheme.
        void Ensure(string name, int score) { if (ratings.All(r => r.Score != score)) ratings.Add(new RatingEntry(name, score, null)); }
        ratings.Add(new RatingEntry("Unrated", null, null));
        Ensure("Approved", 0);
        Ensure("10", 10);
        Ensure("13", 13);
        Ensure("14", 14);
        if (!ratings.Any(r => r.Score >= 21)) ratings.Add(new RatingEntry("21", 21, null));
        Ensure("XXX", 1000);
        Ensure("Banned", 1001);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var r in ratings.OrderBy(r => r.Score).ThenBy(r => r.SubScore))
            {
                w.WriteStartObject();
                w.WriteString("Name", r.Name);
                if (r.Score is { } v) w.WriteNumber("Value", v); else w.WriteNull("Value");
                if (r.Score is { } sc)
                {
                    w.WriteStartObject("RatingScore");
                    w.WriteNumber("score", sc);
                    if (r.SubScore is { } sub) w.WriteNumber("subScore", sub); else w.WriteNull("subScore");
                    w.WriteEndObject();
                }
                else w.WriteNull("RatingScore");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return ms.ToArray();
    }
}
