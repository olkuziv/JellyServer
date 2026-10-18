using System.Net;
using System.Text;
using System.Text.Json;
using JellyServer.Catalog.Contracts;
using JellyServer.Metadata.Contracts;
using JellyServer.Providers.Tmdb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JellyServer.Tests;

public class TmdbTests
{
    private const string MovieJson = """
        {"id":603,"imdb_id":"tt0133093","title":"Матриця","original_title":"The Matrix","overview":"","tagline":"",
         "release_date":"1999-03-30","vote_average":8.24,"vote_count":20000,"runtime":136,
         "genres":[{"id":28,"name":"Бойовик"},{"id":878,"name":"Фантастика"}],
         "production_companies":[{"name":"Warner Bros."},{"name":"Village Roadshow"}],
         "belongs_to_collection":{"id":2344,"name":"The Matrix Collection"},
         "poster_path":"/p.jpg","backdrop_path":"/b.jpg",
         "images":{
           "posters":[{"file_path":"/en.jpg","iso_639_1":"en","vote_average":5,"width":1000},{"file_path":"/uk.jpg","iso_639_1":"uk","vote_average":3,"width":800},{"file_path":"/none.jpg","iso_639_1":null,"vote_average":9,"width":2000}],
           "backdrops":[{"file_path":"/b1.jpg","iso_639_1":"en","vote_average":8,"width":1920},{"file_path":"/b0.jpg","iso_639_1":null,"vote_average":5,"width":1920}],
           "logos":[{"file_path":"/l-en.png","iso_639_1":"en","vote_average":5,"width":500},{"file_path":"/l-uk.png","iso_639_1":"uk","vote_average":1,"width":300}]},
         "credits":{
           "cast":[{"id":2,"name":"Carrie-Anne Moss","character":"Trinity","order":1,"profile_path":"/c.jpg"},{"id":1,"name":"Keanu Reeves","character":"Neo","order":0,"profile_path":"/k.jpg"}],
           "crew":[{"id":10,"name":"Lana Wachowski","job":"Director"},{"id":11,"name":"Joel Silver","job":"Producer"},{"id":12,"name":"Lilly Wachowski","job":"Screenplay"}]},
         "release_dates":{"results":[
           {"iso_3166_1":"US","release_dates":[{"certification":"","type":1},{"certification":"R","type":3}]},
           {"iso_3166_1":"DE","release_dates":[{"certification":"16","type":3}]}]},
         "translations":{"translations":[{"iso_639_1":"en","data":{"title":"The Matrix","overview":"A hacker learns the truth.","tagline":"Free your mind"}}]}}
        """;

    private const string SeriesJson = """
        {"id":1429,"name":"Атака титанів","original_name":"進撃の巨人","overview":"Люди проти титанів.","first_air_date":"2013-04-07","last_air_date":"2023-11-05",
         "status":"Ended","vote_average":8.7,"vote_count":6000,"episode_run_time":[24],
         "genres":[{"name":"Аніме"}],"networks":[{"name":"MBS"},{"name":"NHK"}],"created_by":[{"id":99,"name":"Hajime Isayama"}],
         "external_ids":{"imdb_id":"tt2560140","tvdb_id":267440},
         "content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"},{"iso_3166_1":"DE","rating":"16"}]},
         "poster_path":"/sp.jpg","backdrop_path":"/sb.jpg"}
        """;

    private const string SeasonJson = """
        {"id":1,"name":"Сезон 1","overview":"","air_date":"2013-04-07","poster_path":"/s1.jpg",
         "episodes":[
           {"id":101,"episode_number":1,"name":"","overview":"","air_date":"2013-04-07","vote_average":8.4,"vote_count":10,"runtime":24,"still_path":"/e1.jpg",
            "guest_stars":[{"id":7,"name":"Guest One","character":"Villager","order":0}],"crew":[{"id":8,"name":"Tetsuro Araki","job":"Director"}]},
           {"id":102,"episode_number":2,"name":"Той день","overview":"Опис.","air_date":"2013-04-14","vote_average":0,"vote_count":0}]}
        """;

    private const string SeasonEnglishJson = """
        {"episodes":[{"episode_number":1,"name":"To You, in 2000 Years","overview":"Humanity fights back."},{"episode_number":2,"name":"That Day","overview":"Ignored."}]}
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static TmdbMapSettings Settings(string language = "uk", string country = "UA") => new(language, country, 15, "https://image.tmdb.org/t/p/");

    // ---- mapping ----

    [Fact]
    public void Movie_FillsFieldsAndFallsBackToEnglishText()
    {
        var md = TmdbMapper.Movie(Parse(MovieJson), Settings());

        Assert.Equal("Матриця", md.Name);
        Assert.Equal("The Matrix", md.OriginalTitle);
        Assert.Equal("A hacker learns the truth.", md.Overview); // Ukrainian overview is empty: English one is used
        Assert.Equal("Free your mind", md.Tagline);
        Assert.Equal(new DateTime(1999, 3, 30), md.PremiereDate);
        Assert.Equal(1999, md.ProductionYear);
        Assert.Equal(8.2f, md.CommunityRating);
        Assert.Equal(136 * TimeSpan.TicksPerMinute, md.RuntimeTicks);
        Assert.Equal(["Бойовик", "Фантастика"], md.Genres);
        Assert.Equal(["Warner Bros.", "Village Roadshow"], md.Studios);
        Assert.Equal("The Matrix Collection", md.CollectionName);
        Assert.Equal("603", md.ProviderIds["Tmdb"]);
        Assert.Equal("tt0133093", md.ProviderIds["Imdb"]);
        Assert.Equal("2344", md.ProviderIds["TmdbCollection"]);
    }

    [Theory]
    [InlineData("DE", "DE-16")] // other countries are prefixed
    [InlineData("US", "R")]     // US ratings are kept as they are
    [InlineData("UA", "R")]     // no rating for the country: the US one
    public void Movie_AgeRating_FollowsTheCountry(string country, string expected) =>
        Assert.Equal(expected, TmdbMapper.Movie(Parse(MovieJson), Settings(country: country)).OfficialRating);

    [Fact]
    public void Movie_ArtworkIsRankedByLanguage()
    {
        var uk = TmdbMapper.Movie(Parse(MovieJson), Settings("uk")).Images;
        Assert.Equal("https://image.tmdb.org/t/p/original/uk.jpg", uk.Primary);
        Assert.Equal("https://image.tmdb.org/t/p/original/l-uk.png", uk.Logo);
        // The default backdrop first, then text-free ones, then the rest.
        Assert.Equal(["https://image.tmdb.org/t/p/original/b.jpg", "https://image.tmdb.org/t/p/original/b0.jpg", "https://image.tmdb.org/t/p/original/b1.jpg"], uk.Backdrops);

        var de = TmdbMapper.Movie(Parse(MovieJson), Settings("de", "DE")).Images;
        Assert.Equal("https://image.tmdb.org/t/p/original/en.jpg", de.Primary); // no German poster: English next
        Assert.Equal("https://image.tmdb.org/t/p/original/l-en.png", de.Logo);
    }

    [Fact]
    public void Movie_PeopleAreActorsInBillingOrderThenCrew()
    {
        var people = TmdbMapper.Movie(Parse(MovieJson), Settings()).People!;
        Assert.Equal(["Keanu Reeves", "Carrie-Anne Moss", "Lana Wachowski", "Lilly Wachowski", "Joel Silver"], people.Select(p => p.Name));
        var neo = people[0];
        Assert.Equal(("Actor", "Neo", "1"), (neo.Type, neo.Role, neo.ProviderId));
        Assert.Equal("https://image.tmdb.org/t/p/w500/k.jpg", neo.ImageUrl);
        Assert.Equal("Director", people[2].Type);
        Assert.Equal("Writer", people[3].Type);
        Assert.Equal("Producer", people[4].Type);
    }

    [Fact]
    public void Series_MapsNetworksCreatorsAndEndDate()
    {
        var md = TmdbMapper.Series(Parse(SeriesJson), Settings("uk", "US"));
        Assert.Equal("Атака титанів", md.Name);
        Assert.Equal("進撃の巨人", md.OriginalTitle);
        Assert.Equal(["MBS", "NHK"], md.Studios);
        Assert.Equal(new DateTime(2023, 11, 5), md.EndDate); // ended: the last air date is the end
        Assert.Equal("TV-MA", md.OfficialRating);
        Assert.Equal(24 * TimeSpan.TicksPerMinute, md.RuntimeTicks);
        Assert.Equal("267440", md.ProviderIds["Tvdb"]);
        Assert.Contains(md.People!, p => p is { Name: "Hajime Isayama", Type: "Creator" });
        Assert.Equal("https://image.tmdb.org/t/p/original/sp.jpg", md.Images.Primary);
    }

    [Theory]
    [InlineData("uk", null)] // no Ukrainian title: TMDB repeats the Japanese one, so the local (folder/NFO) name stays
    [InlineData("en", "I'm Dating a Dark Summoner")]
    [InlineData("ja", "ダークサモナーとデキている")] // Japanese is the preferred language: keep it
    public void Series_NoTranslation_LeavesTheNameToTheLocalFiles(string language, string? expected)
    {
        const string json = """
            {"id":1,"name":"ダークサモナーとデキている","original_name":"ダークサモナーとデキている","original_language":"ja",
             "translations":{"translations":[{"iso_639_1":"en","data":{"name":"I'm Dating a Dark Summoner","overview":"Amona"}}]}}
            """;
        var md = TmdbMapper.Series(Parse(json), Settings(language));
        Assert.Equal(expected, md.Name);
        if (language == "uk") Assert.Equal("ダークサモナーとデキている", md.OriginalTitle);
    }

    [Fact]
    public void Season_MapsEpisodesByNumber_AndTakesMissingTextFromEnglish()
    {
        var (season, episodes) = TmdbMapper.Season(Parse(SeasonJson), Parse(SeasonEnglishJson), Settings());

        Assert.Equal("Сезон 1", season.Name);
        Assert.Equal("https://image.tmdb.org/t/p/original/s1.jpg", season.Images.Primary);
        Assert.Equal(2, episodes.Count);

        var one = episodes[1];
        Assert.Equal("To You, in 2000 Years", one.Name);       // empty in Ukrainian
        Assert.Equal("Humanity fights back.", one.Overview);
        Assert.Equal(8.4f, one.CommunityRating);
        Assert.Equal(24 * TimeSpan.TicksPerMinute, one.RuntimeTicks);
        Assert.Equal("https://image.tmdb.org/t/p/original/e1.jpg", one.Images.Primary);
        Assert.Contains(one.People!, p => p is { Name: "Guest One", Type: "GuestStar" });
        Assert.Contains(one.People!, p => p is { Name: "Tetsuro Araki", Type: "Director" });

        var two = episodes[2];
        Assert.Equal("Той день", two.Name);                     // the preferred language wins when present
        Assert.Null(two.CommunityRating);                       // no votes: no rating
    }

    // ---- matching by name ----

    private static JsonElement[] Results(string json) => Parse(json).EnumerateArray().ToArray();

    [Fact]
    public void Matcher_AcceptsEqualTitleAndYear_AndPrefersPopular()
    {
        var results = Results("""[{"id":1,"title":"Alpha","release_date":"2019-01-01","popularity":1},{"id":2,"title":"alpha!","release_date":"2020-01-01","popularity":50},{"id":3,"title":"Alpha","release_date":"2001-01-01","popularity":99}]""");
        var hit = TmdbMatcher.Pick(results, "Alpha", 2019, series: false);
        Assert.Equal("2", hit!.Value.GetProperty("id").ToString()); // the year may differ by one; 2001 is too far
    }

    [Fact]
    public void Matcher_ComparesOriginalTitleToo()
    {
        var results = Results("""[{"id":7,"name":"Атака титанів","original_name":"Shingeki no Kyojin","first_air_date":"2013-04-07","popularity":10}]""");
        Assert.NotNull(TmdbMatcher.Pick(results, "Shingeki no Kyojin", 2013, series: true));
    }

    [Fact]
    public void Matcher_RefusesAWrongMatch()
    {
        var results = Results("""[{"id":1,"title":"Alpha Centauri","release_date":"2019-01-01","popularity":90}]""");
        Assert.Null(TmdbMatcher.Pick(results, "Alpha", 2019, series: false)); // contained, but too short for the loose pass
        Assert.Null(TmdbMatcher.Pick(results, "Beta", 2019, series: false));
    }

    [Fact]
    public void Matcher_LoosePass_NeedsTheExactYear()
    {
        var results = Results("""[{"id":1,"title":"The Matrix Reloaded","release_date":"2003-05-15","popularity":10}]""");
        Assert.NotNull(TmdbMatcher.Pick(results, "Matrix Reloaded", 2003, series: false));
        Assert.Null(TmdbMatcher.Pick(results, "Matrix Reloaded", 2004, series: false));
        Assert.Null(TmdbMatcher.Pick(results, "Matrix Reloaded", null, series: false));
    }

    // ---- the provider over a fake service ----

    private sealed class FakeTmdb : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (TmdbMetadataProvider Provider, FakeTmdb Fake, TmdbClient Client) Create(Func<HttpRequestMessage, HttpResponseMessage?> respond)
    {
        var fake = new FakeTmdb { Respond = respond };
        var options = Options.Create(new TmdbOptions { ApiKey = "key", RequestsPerSecond = 1000 });
        var client = new TmdbClient(new HttpClient(fake), options, NullLogger<TmdbClient>.Instance);
        return (new TmdbMetadataProvider(client, options, NullLogger<TmdbMetadataProvider>.Instance), fake, client);
    }

    private static MetadataRequest Movie(string name, int? year, params (string Key, string Value)[] ids)
    {
        var dict = ids.ToDictionary(i => i.Key, i => i.Value);
        return new(new ItemRef(Guid.NewGuid(), ItemTypes.Movie, name, year, Guid.NewGuid(), null, null, null, dict), dict, new MetadataContext("uk", "UA"));
    }

    [Fact]
    public async Task Provider_WithATmdbId_FetchesDirectly()
    {
        var (provider, fake, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/3/movie/603" => FakeTmdb.Json(MovieJson),
            "/3/collection/2344" => FakeTmdb.Json("""{"id":2344,"name":"Матриця (колекція)"}"""),
            _ => null,
        });
        var result = await provider.FetchAsync(Movie("anything", null, ("Tmdb", "603")), CancellationToken.None);

        Assert.Equal(ProviderStatus.Found, result.Status);
        Assert.Equal("Матриця", result.Metadata!.Name);
        Assert.Equal(2, fake.Requests.Count);
        var request = fake.Requests[0];
        Assert.Contains("api_key=key", request);
        Assert.Contains("language=uk", request);
        Assert.Contains("append_to_response=credits", request);
    }

    [Fact]
    public async Task Provider_NamesTheCollectionInTheLibraryLanguage_AndAsksOncePerCollection()
    {
        var (provider, fake, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/3/movie/603" => FakeTmdb.Json(MovieJson),
            "/3/collection/2344" => FakeTmdb.Json("""{"id":2344,"name":"Матриця (колекція)"}"""),
            _ => null,
        });
        var first = await provider.FetchAsync(Movie("a", null, ("Tmdb", "603")), CancellationToken.None);
        var second = await provider.FetchAsync(Movie("b", null, ("Tmdb", "603")), CancellationToken.None);

        Assert.Equal("Матриця (колекція)", first.Metadata!.CollectionName);
        Assert.Equal("Матриця (колекція)", second.Metadata!.CollectionName);
        Assert.Single(fake.Requests, q => q.StartsWith("/3/collection/2344"));
    }

    [Fact]
    public async Task Provider_KeepsTheEnglishCollectionName_WhenTheCollectionPageIsMissing()
    {
        var (provider, _, _) = Create(r => r.RequestUri!.AbsolutePath == "/3/movie/603" ? FakeTmdb.Json(MovieJson) : null);
        var result = await provider.FetchAsync(Movie("a", null, ("Tmdb", "603")), CancellationToken.None);

        Assert.Equal("The Matrix Collection", result.Metadata!.CollectionName);
    }

    [Fact]
    public async Task Provider_WithAnImdbId_TranslatesItThroughFind()
    {
        var (provider, fake, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/3/find/tt0133093" => FakeTmdb.Json("""{"movie_results":[{"id":603}],"tv_results":[]}"""),
            "/3/movie/603" => FakeTmdb.Json(MovieJson),
            _ => null,
        });
        var result = await provider.FetchAsync(Movie("x", null, ("Imdb", "tt0133093")), CancellationToken.None);

        Assert.Equal(ProviderStatus.Found, result.Status);
        Assert.Contains(fake.Requests, q => q.StartsWith("/3/find/tt0133093") && q.Contains("external_source=imdb_id"));
    }

    [Fact]
    public async Task Provider_SearchesByNameAndYear_ThenWithoutYear()
    {
        var (provider, fake, _) = Create(r =>
        {
            var q = r.RequestUri!.Query;
            if (r.RequestUri.AbsolutePath == "/3/search/movie")
                return FakeTmdb.Json(q.Contains("year=") ? """{"results":[]}""" : """{"results":[{"id":603,"title":"Матриця","original_title":"The Matrix","release_date":"1999-03-30","popularity":5}]}""");
            return r.RequestUri.AbsolutePath == "/3/movie/603" ? FakeTmdb.Json(MovieJson) : null;
        });
        var result = await provider.FetchAsync(Movie("The Matrix", 1998), CancellationToken.None);

        Assert.Equal(ProviderStatus.Found, result.Status);
        Assert.Equal(["/3/search/movie", "/3/search/movie", "/3/movie/603", "/3/collection/2344"], fake.Requests.Select(q => q.Split('?')[0]));
    }

    [Fact]
    public async Task Provider_DoesNotTakeAWrongSearchResult()
    {
        var (provider, fake, _) = Create(r => r.RequestUri!.AbsolutePath == "/3/search/movie"
            ? FakeTmdb.Json("""{"results":[{"id":9,"title":"Something Else","release_date":"2019-01-01","popularity":99}]}""") : null);
        var result = await provider.FetchAsync(Movie("Alpha", 2019), CancellationToken.None);

        Assert.Equal(ProviderStatus.NotFound, result.Status);
        Assert.DoesNotContain(fake.Requests, q => q.StartsWith("/3/movie/"));
    }

    [Fact]
    public async Task Provider_UnknownId_IsNotFound()
    {
        var (provider, _, _) = Create(_ => null); // everything is 404
        Assert.Equal(ProviderStatus.NotFound, (await provider.FetchAsync(Movie("x", null, ("Tmdb", "1")), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Provider_RejectedKey_SwitchesItselfOff()
    {
        var (provider, fake, client) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.True(provider.IsAvailable);

        var result = await provider.FetchAsync(Movie("x", null, ("Tmdb", "1")), CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, result.Status);
        Assert.False(provider.IsAvailable);
        Assert.True(client.Unauthorized);
        Assert.Single(fake.Requests); // no hammering with a bad key
    }

    [Fact]
    public async Task Client_RetriesAfterRateLimit()
    {
        var calls = 0;
        var (provider, fake, _) = Create(_ =>
        {
            if (++calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new(TimeSpan.Zero);
                return limited;
            }
            return FakeTmdb.Json(MovieJson);
        });
        var result = await provider.FetchAsync(Movie("x", null, ("Tmdb", "603")), CancellationToken.None);

        Assert.Equal(ProviderStatus.Found, result.Status);
        Assert.Equal(2, fake.Requests.Count(q => q.StartsWith("/3/movie/603"))); // the rate-limited try and the retry
    }

    [Fact]
    public async Task Provider_Season_AsksEnglishOnlyWhenTextIsMissing()
    {
        var (provider, fake, _) = Create(r =>
            r.RequestUri!.AbsolutePath == "/3/tv/1429/season/1"
                ? FakeTmdb.Json(r.RequestUri.Query.Contains("language=en-US") ? SeasonEnglishJson : SeasonJson)
                : null);
        var series = new ItemRef(Guid.NewGuid(), ItemTypes.Series, "Атака титанів", 2013, Guid.NewGuid(), null, null, null, new Dictionary<string, string>());
        var request = new SeasonRequest(series, new Dictionary<string, string> { ["Tmdb"] = "1429" }, 1, new MetadataContext("uk", "UA"));

        var result = await provider.FetchSeasonAsync(request, CancellationToken.None);

        Assert.Equal(ProviderStatus.Found, result.Status);
        Assert.Equal("To You, in 2000 Years", result.Episodes[1].Name);
        Assert.Equal(2, fake.Requests.Count); // preferred language + one English request

        fake.Requests.Clear();
        await provider.FetchSeasonAsync(request with { Context = new MetadataContext("en", "US") }, CancellationToken.None);
        Assert.Single(fake.Requests); // English was asked for already
    }
}