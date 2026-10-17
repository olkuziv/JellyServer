using System.Text.Json;

namespace JellyServer.Providers.Tmdb;

/// <summary>Chooses the right search result for an item known only by its name and year.</summary>
public static class TmdbMatcher
{
    /// <summary>Lower-case letters and digits only, so "Spider-Man: Homecoming" equals "spider man homecoming".</summary>
    public static string Normalize(string? text) => new([.. (text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// A result is accepted when its title or original title equals the name (ignoring case and punctuation) and, when the year is known,
    /// the release year differs by at most one. A second, looser pass needs the exact year and ignores a leading article and a subtitle
    /// after a colon ("The Matrix Reloaded" = "Matrix Reloaded", "Spider-Man: Homecoming" = "Spider-Man"), but never extra words.
    /// Among several acceptable results the most popular wins. Null when nothing fits: a wrong poster is worse than none.
    /// </summary>
    public static JsonElement? Pick(IEnumerable<JsonElement> results, string name, int? year, bool series)
    {
        var wanted = Normalize(name);
        if (wanted.Length == 0) return null;
        var list = results.ToList();

        string?[] Titles(JsonElement r) => series ? [r.Str("name"), r.Str("original_name")] : [r.Str("title"), r.Str("original_title")];
        int? Year(JsonElement r) => (series ? r.Date("first_air_date") : r.Date("release_date"))?.Year;
        bool YearOk(JsonElement r, int tolerance) => year is null || Year(r) is not { } y || Math.Abs(y - year.Value) <= tolerance;
        double Popularity(JsonElement r) => r.Dbl("popularity") ?? 0;

        var exact = list.Where(r => Titles(r).Any(t => Normalize(t) == wanted) && YearOk(r, 1)).OrderByDescending(Popularity).ToList();
        if (exact.Count > 0) return exact[0];

        if (year is not null && LooseKey(name) is { Length: >= 3 } key)
        {
            var loose = list.Where(r => Year(r) == year && Titles(r).Any(t => LooseKey(t) == key)).OrderByDescending(Popularity).ToList();
            if (loose.Count > 0) return loose[0];
        }
        return null;
    }

    private static readonly string[] Articles = ["the ", "a ", "an "];

    /// <summary>The comparison key of the loose pass: no leading article, nothing after a colon.</summary>
    private static string LooseKey(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        var colon = t.IndexOf(':');
        if (colon > 0) t = t[..colon];
        foreach (var a in Articles)
            if (t.StartsWith(a, StringComparison.Ordinal)) { t = t[a.Length..]; break; }
        return Normalize(t);
    }
}