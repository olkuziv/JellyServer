using System.Text.Json;
using JellyServer.Catalog.Contracts;
using Kind = JellyServer.Catalog.Contracts.ItemTypes;
using JellyServer.Metadata.Contracts;
using Microsoft.Extensions.Options;

namespace JellyServer.Providers.Tmdb;

/// <summary>
/// The Movie Database as a metadata and artwork source. The item is identified by (in this order) a TMDB id, an IMDb or TVDB id
/// (translated through TMDB's /find), or a search by name and year accepted only when the name and year really match.
/// </summary>
public sealed class TmdbMetadataProvider : IMetadataProvider
{
    private static readonly string[] Types = [Kind.Movie, Kind.Series, Kind.Season, Kind.Episode];
    private static readonly string[] Images = ["Primary", "Backdrop", "Logo"];

    private readonly TmdbClient _client;
    private readonly TmdbOptions _opt;
    private readonly ILogger<TmdbMetadataProvider> _log;

    public TmdbMetadataProvider(TmdbClient client, IOptions<TmdbOptions> options, ILogger<TmdbMetadataProvider> log)
    {
        _client = client;
        _opt = options.Value;
        _log = log;
        if (!client.HasKey) log.LogInformation("TMDB metadata is off: set TMDB_API_KEY (an API key or a read access token from themoviedb.org) to enable it");
    }

    public string Name => "TheMovieDb";

    public bool IsAvailable => _client.HasKey && !_client.Unauthorized;

    public IReadOnlyCollection<string> ItemTypes => Types;

    public IReadOnlyCollection<string> ImageTypes => Images;

    private TmdbMapSettings Settings(MetadataContext c) => new(c.Language, c.Country, _opt.MaxCast, _opt.ImageBaseUrl);

    private static Dictionary<string, string?> Query(MetadataContext c, string? append = null)
    {
        var lang = c.Language.Length >= 2 ? c.Language[..2].ToLowerInvariant() : "en";
        return new()
        {
            ["language"] = c.Language, ["append_to_response"] = append, ["include_image_language"] = $"{lang},en,null",
        };
    }

    public async Task<ProviderResult> FetchAsync(MetadataRequest request, CancellationToken ct)
    {
        if (request.Item.Type is not (Kind.Movie or Kind.Series)) return ProviderResult.NotFound;
        var series = request.Item.Type == Kind.Series;
        try
        {
            var id = await ResolveAsync(series, request.ProviderIds, request.Item.Name, request.Item.ProductionYear, request.Context, ct);
            if (id is null) return ProviderResult.NotFound;
            using var doc = series
                ? await _client.GetAsync($"/tv/{id}", Query(request.Context, "credits,content_ratings,images,external_ids,translations"), ct)
                : await _client.GetAsync($"/movie/{id}", Query(request.Context, "credits,release_dates,images,external_ids,translations"), ct);
            if (doc is null) return ProviderResult.NotFound;
            var md = series ? TmdbMapper.Series(doc.RootElement, Settings(request.Context)) : TmdbMapper.Movie(doc.RootElement, Settings(request.Context));
            return new ProviderResult(ProviderStatus.Found, md);
        }
        catch (TmdbUnauthorizedException)
        {
            return ProviderResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("TMDB request for {Name} failed: {Error}", request.Item.Name, ex.Message);
            return ProviderResult.Failed;
        }
    }

    public async Task<SeasonProviderResult> FetchSeasonAsync(SeasonRequest request, CancellationToken ct)
    {
        try
        {
            var seriesId = await ResolveAsync(true, request.SeriesProviderIds, request.Series.Name, request.Series.ProductionYear, request.Context, ct);
            if (seriesId is null) return SeasonProviderResult.NotFound;
            var path = $"/tv/{seriesId}/season/{request.SeasonNumber}";
            using var doc = await _client.GetAsync(path, Query(request.Context, "images"), ct);
            if (doc is null) return SeasonProviderResult.NotFound;

            // The preferred language may lack some episode names or overviews: fill them from English with one more request.
            JsonDocument? english = null;
            try
            {
                var lacking = doc.RootElement.Items("episodes").Any(e => e.Str("overview") is null || e.Str("name") is null);
                if (lacking && !request.Context.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    english = await _client.GetAsync(path, new Dictionary<string, string?> { ["language"] = "en-US" }, ct);
                var (season, episodes) = TmdbMapper.Season(doc.RootElement, english?.RootElement, Settings(request.Context));
                return new SeasonProviderResult(ProviderStatus.Found, season, episodes);
            }
            finally
            {
                english?.Dispose();
            }
        }
        catch (TmdbUnauthorizedException)
        {
            return SeasonProviderResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("TMDB request for {Name} season {Season} failed: {Error}", request.Series.Name, request.SeasonNumber, ex.Message);
            return SeasonProviderResult.Failed;
        }
    }

    /// <summary>The TMDB id of the item, or null when it cannot be told.</summary>
    private async Task<string?> ResolveAsync(bool series, IReadOnlyDictionary<string, string> ids, string name, int? year, MetadataContext context, CancellationToken ct)
    {
        if (ids.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb)) return tmdb;

        foreach (var (key, source) in new[] { ("Imdb", "imdb_id"), ("Tvdb", "tvdb_id") })
        {
            if (!ids.TryGetValue(key, out var external) || string.IsNullOrWhiteSpace(external)) continue;
            if (key == "Tvdb" && !series) continue;
            using var found = await _client.GetAsync($"/find/{Uri.EscapeDataString(external)}", new Dictionary<string, string?> { ["external_source"] = source }, ct);
            if (found?.RootElement.Items(series ? "tv_results" : "movie_results").FirstOrDefault().IdText("id") is { } id) return id;
        }

        var kind = series ? "tv" : "movie";
        var yearKey = series ? "first_air_date_year" : "year";
        foreach (var withYear in year is null ? new[] { false } : new[] { true, false })
        {
            using var search = await _client.GetAsync($"/search/{kind}", new Dictionary<string, string?>
            {
                ["query"] = name, ["language"] = context.Language, ["include_adult"] = "false",
                [yearKey] = withYear ? year!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            }, ct);
            if (search is null) continue;
            if (TmdbMatcher.Pick(search.RootElement.Items("results"), name, year, series) is { } hit && hit.IdText("id") is { } id) return id;
        }
        return null;
    }
}