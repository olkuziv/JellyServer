using System.Globalization;
using System.Text.Json;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Providers.Tmdb;

/// <param name="Language">Preferred language, "uk" or "uk-UA".</param>
/// <param name="Country">Country whose age rating is wanted ("UA").</param>
public sealed record TmdbMapSettings(string Language, string Country, int MaxCast, string ImageBase)
{
    /// <summary>The two-letter language used to rank artwork ("uk").</summary>
    public string Lang => Language.Length >= 2 ? Language[..2].ToLowerInvariant() : "en";

    public string Image(string path, string size = "original") => ImageBase.TrimEnd('/') + "/" + size + path;
}

/// <summary>Turns TMDB's answers into <see cref="RemoteMetadata"/> (pure functions, no I/O).</summary>
public static class TmdbMapper
{
    public static RemoteMetadata Movie(JsonElement root, TmdbMapSettings s)
    {
        var en = Translation(root, "en");
        var title = Title(root, "title", "original_title", en, s);
        var original = root.Str("original_title");
        var date = root.Date("release_date");
        var md = new RemoteMetadata
        {
            Name = title,
            OriginalTitle = original != title ? original : null,
            Overview = root.Str("overview") ?? en?.Str("overview"),
            Tagline = root.Str("tagline") ?? en?.Str("tagline"),
            PremiereDate = date, ProductionYear = date?.Year,
            CommunityRating = Rating(root),
            RuntimeTicks = Minutes(root.Int("runtime")),
            OfficialRating = MovieCertification(root, s.Country),
            Genres = Names(root, "genres"),
            Studios = Names(root, "production_companies"),
            CollectionName = root.Get("belongs_to_collection")?.Str("name"),
            People = People(root, s, series: false),
            Images = Images(root, s, root.Str("poster_path"), root.Str("backdrop_path")),
        };
        Id(md, "Tmdb", root.IdText("id"));
        Id(md, "Imdb", root.Str("imdb_id") ?? root.Get("external_ids")?.Str("imdb_id"));
        Id(md, "Tvdb", root.Get("external_ids")?.IdText("tvdb_id"));
        Id(md, "TmdbCollection", root.Get("belongs_to_collection")?.IdText("id"));
        return md;
    }

    public static RemoteMetadata Series(JsonElement root, TmdbMapSettings s)
    {
        var en = Translation(root, "en");
        var title = Title(root, "name", "original_name", en, s);
        var original = root.Str("original_name");
        var first = root.Date("first_air_date");
        var ended = root.Str("status") is "Ended" or "Canceled";
        var md = new RemoteMetadata
        {
            Name = title,
            OriginalTitle = original != title ? original : null,
            Overview = root.Str("overview") ?? en?.Str("overview"),
            Tagline = root.Str("tagline") ?? en?.Str("tagline"),
            PremiereDate = first, ProductionYear = first?.Year,
            EndDate = ended ? root.Date("last_air_date") : null,
            CommunityRating = Rating(root),
            RuntimeTicks = Minutes(root.Items("episode_run_time").Select(e => e.TryGetInt32(out var n) ? n : 0).FirstOrDefault(n => n > 0)),
            OfficialRating = SeriesCertification(root, s.Country),
            Genres = Names(root, "genres"),
            Studios = Names(root, "networks") is { Length: > 0 } networks ? networks : Names(root, "production_companies"),
            People = People(root, s, series: true),
            Images = Images(root, s, root.Str("poster_path"), root.Str("backdrop_path")),
        };
        Id(md, "Tmdb", root.IdText("id"));
        Id(md, "Imdb", root.Get("external_ids")?.Str("imdb_id"));
        Id(md, "Tvdb", root.Get("external_ids")?.IdText("tvdb_id"));
        return md;
    }

    /// <summary>A season of a series, and its episodes by episode number.</summary>
    /// <param name="english">The same season requested in English, used for names and overviews the preferred language lacks.</param>
    public static (RemoteMetadata Season, Dictionary<int, RemoteMetadata> Episodes) Season(JsonElement root, JsonElement? english, TmdbMapSettings s)
    {
        var date = root.Date("air_date");
        var season = new RemoteMetadata
        {
            Name = root.Str("name"), Overview = root.Str("overview") ?? english?.Str("overview"),
            PremiereDate = date, ProductionYear = date?.Year,
            Images = Images(root, s, root.Str("poster_path"), null),
        };

        var englishEpisodes = (english?.Items("episodes") ?? []).Where(e => e.Int("episode_number") is not null).ToDictionary(e => e.Int("episode_number")!.Value);
        var episodes = new Dictionary<int, RemoteMetadata>();
        foreach (var e in root.Items("episodes"))
        {
            if (e.Int("episode_number") is not { } number) continue;
            englishEpisodes.TryGetValue(number, out var en);
            var air = e.Date("air_date");
            var md = new RemoteMetadata
            {
                Name = e.Str("name") ?? en.Str("name"), Overview = e.Str("overview") ?? en.Str("overview"),
                PremiereDate = air, ProductionYear = air?.Year, CommunityRating = Rating(e), RuntimeTicks = Minutes(e.Int("runtime")),
                People = EpisodePeople(e, s),
            };
            if (e.Str("still_path") is { } still) md.Images.Primary = s.Image(still);
            Id(md, "Tmdb", e.IdText("id"));
            episodes[number] = md;
        }
        return (season, episodes);
    }

    // ---- pieces ----

    private static void Id(RemoteMetadata md, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value) && value != "0") md.ProviderIds[key] = value;
    }

    /// <summary>
    /// The title in the preferred language, or null when TMDB has none: it then answers with the original title (Japanese, ...), and
    /// the item keeps the name from its folder or NFO instead. For English an English translation is good enough.
    /// </summary>
    private static string? Title(JsonElement root, string field, string originalField, JsonElement? en, TmdbMapSettings s)
    {
        var title = root.Str(field);
        var original = root.Str(originalField);
        var originalIsPreferred = string.Equals(root.Str("original_language"), s.Lang, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(title) && (title != original || originalIsPreferred)) return title;
        return s.Lang == "en" ? en?.Str(field) ?? title : null;
    }

    private static JsonElement? Translation(JsonElement root, string language) =>
        root.Get("translations")?.Items("translations").FirstOrDefault(t => t.Str("iso_639_1") == language).Get("data");

    private static float? Rating(JsonElement e) => e.Dbl("vote_average") is { } v && v > 0 && (e.Int("vote_count") ?? 1) > 0 ? (float)Math.Round(v, 1) : null;

    private static long? Minutes(int? minutes) => minutes is > 0 ? minutes.Value * TimeSpan.TicksPerMinute : null;

    private static string[] Names(JsonElement root, string array) =>
        [.. root.Items(array).Select(g => g.Str("name")).OfType<string>().Distinct()];

    /// <summary>Jellyfin shows US ratings as they are ("PG-13") and others with the country ("DE-12").</summary>
    private static string Format(string country, string certification) => country == "US" ? certification : $"{country}-{certification}";

    private static string? MovieCertification(JsonElement root, string country)
    {
        var results = root.Get("release_dates")?.Items("results").ToList() ?? [];
        string? For(string c)
        {
            var releases = results.FirstOrDefault(r => r.Str("iso_3166_1") == c).Items("release_dates")
                .Where(r => r.Str("certification") is not null).ToList();
            var best = releases.FirstOrDefault(r => r.Int("type") == 3) is { ValueKind: JsonValueKind.Object } theatrical ? theatrical : releases.FirstOrDefault();
            return best.ValueKind == JsonValueKind.Object ? best.Str("certification") is { } cert ? Format(c, cert) : null : null;
        }
        return For(country) ?? (country == "US" ? null : For("US"));
    }

    private static string? SeriesCertification(JsonElement root, string country)
    {
        var results = root.Get("content_ratings")?.Items("results").ToList() ?? [];
        string? For(string c) => results.FirstOrDefault(r => r.Str("iso_3166_1") == c).Str("rating") is { } r ? Format(c, r) : null;
        return For(country) ?? (country == "US" ? null : For("US"));
    }

    private static List<Person> People(JsonElement root, TmdbMapSettings s, bool series)
    {
        var people = new List<Person>();
        var credits = root.Get("credits");
        if (credits is { } c)
        {
            foreach (var a in c.Items("cast").OrderBy(a => a.Int("order") ?? int.MaxValue).Take(s.MaxCast))
                if (a.Str("name") is { } name)
                    people.Add(new Person(name, a.Str("character"), "Actor") { ProviderId = a.IdText("id"), ImageUrl = a.Str("profile_path") is { } pp ? s.Image(pp, "w500") : null });

            var crew = c.Items("crew").ToList();
            AddCrew(people, s, crew.Where(m => m.Str("job") == "Director"), "Director", "Director", int.MaxValue);
            AddCrew(people, s, crew.Where(m => m.Str("job") is "Screenplay" or "Writer" or "Story" or "Novel" or "Teleplay"), "Writer", null, 5);
            AddCrew(people, s, crew.Where(m => m.Str("job") == "Producer"), "Producer", "Producer", 5);
        }
        if (series)
            foreach (var creator in root.Items("created_by"))
                if (creator.Str("name") is { } name && !people.Any(p => p.Name == name && p.Type == "Creator"))
                    people.Add(new Person(name, "Creator", "Creator") { ProviderId = creator.IdText("id"), ImageUrl = creator.Str("profile_path") is { } pp ? s.Image(pp, "w500") : null });
        return people;
    }

    private static void AddCrew(List<Person> people, TmdbMapSettings s, IEnumerable<JsonElement> members, string type, string? role, int limit)
    {
        foreach (var m in members.Take(limit * 3))
        {
            if (m.Str("name") is not { } name || people.Any(p => p.Name == name && p.Type == type)) continue;
            if (people.Count(p => p.Type == type) >= limit) break;
            people.Add(new Person(name, role ?? m.Str("job"), type) { ProviderId = m.IdText("id"), ImageUrl = m.Str("profile_path") is { } pp ? s.Image(pp, "w500") : null });
        }
    }

    private static List<Person> EpisodePeople(JsonElement episode, TmdbMapSettings s)
    {
        var people = new List<Person>();
        foreach (var g in episode.Items("guest_stars").OrderBy(g => g.Int("order") ?? int.MaxValue).Take(s.MaxCast))
            if (g.Str("name") is { } name)
                people.Add(new Person(name, g.Str("character"), "GuestStar") { ProviderId = g.IdText("id"), ImageUrl = g.Str("profile_path") is { } pp ? s.Image(pp, "w500") : null });
        AddCrew(people, s, episode.Items("crew").Where(m => m.Str("job") == "Director"), "Director", "Director", int.MaxValue);
        AddCrew(people, s, episode.Items("crew").Where(m => m.Str("job") is "Writer" or "Screenplay" or "Teleplay"), "Writer", null, 5);
        return people;
    }

    // ---- artwork ----

    private static ItemImages Images(JsonElement root, TmdbMapSettings s, string? poster, string? backdrop)
    {
        var images = root.Get("images");
        var result = new ItemImages();
        var posters = Rank(images?.Items("posters"), s.Lang, noLanguageFirst: false);
        if ((posters.FirstOrDefault() ?? poster) is { } p) result.Primary = s.Image(p);
        var backdrops = Rank(images?.Items("backdrops"), s.Lang, noLanguageFirst: true).ToList();
        if (backdrop is not null && !backdrops.Contains(backdrop)) backdrops.Insert(0, backdrop);
        result.Backdrops = [.. backdrops.Take(10).Select(b => s.Image(b))];
        if (Rank(images?.Items("logos"), s.Lang, noLanguageFirst: false).FirstOrDefault() is { } logo) result.Logo = s.Image(logo);
        return result;
    }

    /// <summary>File paths ordered by language preference, then votes, then size.</summary>
    private static IEnumerable<string> Rank(IEnumerable<JsonElement>? list, string lang, bool noLanguageFirst)
    {
        int LanguageRank(JsonElement i)
        {
            var l = i.Str("iso_639_1");
            return noLanguageFirst
                ? l is null ? 0 : l == lang ? 1 : l == "en" ? 2 : 3
                : l == lang ? 0 : l == "en" ? 1 : l is null ? 2 : 3;
        }
        return (list ?? []).Where(i => i.Str("file_path") is not null)
            .OrderBy(LanguageRank).ThenByDescending(i => i.Dbl("vote_average") ?? 0).ThenByDescending(i => i.Int("width") ?? 0)
            .Select(i => i.Str("file_path")!);
    }
}