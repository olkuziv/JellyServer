using JellyServer.Scanning;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("Ґанц (2010)", "Ґанц", 2010)]
    [InlineData("[01] Pacific Rim (2013) BDRip 1080p H.265 [UKR_ENG] [Hurtom]", "Pacific Rim", 2013)]
    [InlineData("[02] Pacific Rim. Uprising (2018) BDRip 1080p H.265 [UKR_ENG] [Hurtom]", "Pacific Rim. Uprising", 2018)]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264", "The Matrix", 1999)]
    [InlineData("Foundation", "Foundation", null)]
    [InlineData("12 Angry Men (1957)", "12 Angry Men", 1957)]
    [InlineData("300 (2006)", "300", 2006)]
    [InlineData("10 Cloverfield Lane (2016) BDRip", "10 Cloverfield Lane", 2016)]
    [InlineData("01 - Pacific Rim (2013)", "Pacific Rim", 2013)]
    [InlineData("2. Pacific Rim Uprising (2018)", "Pacific Rim Uprising", 2018)]
    [InlineData("А інші згорять у пеклі (2023)", "А інші згорять у пеклі", 2023)]
    public void ParsesMovieNames(string input, string title, int? year)
    {
        var (t, y) = Naming.ParseMovieName(input);
        Assert.Equal(title, t);
        Assert.Equal(year, y);
    }

    [Theory]
    [InlineData("Foundation - S01E01 (2021) WEBRip 1080p [Ukr.Eng] [Цікава Ідея]", 1, 1, null)]
    [InlineData("А інші згорять у пеклі (2023) S02E04 - hdvb", 2, 4, null)]
    [InlineData("S01 E02 Нова надія", 1, 2, "Нова надія")]
    [InlineData("Dandadan - S01E12 (BDRip 1080p x265 60fps)", 1, 12, null)]
    [InlineData("Дандадан 2 - 01", null, 1, null)]
    [InlineData("1x05 The Pilot", 1, 5, "The Pilot")]
    [InlineData("07 Серія", null, 7, "Серія")]
    public void ParsesEpisodes(string input, int? season, int? episode, string? title)
    {
        var info = Naming.ParseEpisode(input);
        Assert.Equal(season, info.Season);
        Assert.Equal(episode, info.Episode);
        Assert.Equal(title, Naming.EpisodeTitle(info.Rest));
    }

    [Theory]
    [InlineData("Season 1 - 1080p", 1)]
    [InlineData("Season 01", 1)]
    [InlineData("2", 2)]
    [InlineData("S03", 3)]
    [InlineData("2 сезон", 2)]
    [InlineData("Specials", 0)]
    [InlineData("Extras Stuff", null)]
    public void ParsesSeasonFolders(string input, int? season) => Assert.Equal(season, Naming.ParseSeasonDir(input));

    [Theory]
    [InlineData("Ґанц", "анц", false)]          // chopped mid-word: generator bug, ignore
    [InlineData("The Matrix", "Matrix", true)]  // leading article removed: fine
    [InlineData("Alien", "Alien 1", true)]
    [InlineData("Alien", "", false)]
    public void ValidatesSortTitles(string title, string sortTitle, bool valid) =>
        Assert.Equal(valid, ItemNaming.IsValidSortTitle(title, sortTitle));

    [Fact]
    public void SearchTextNormalisesUkrainian() =>
        Assert.Equal("ганц gantz", ItemNaming.SearchText("Ґанц", "Gantz"));

    [Theory]
    [InlineData("https://ashdi.vip/video22/2/new/x/hls/abc==/index.m3u8", "hls")]
    [InlineData("https://cdn.example.com/movie.mp4?token=1", "mp4")]
    [InlineData("https://s30.hdvbua.pro/media2/hls/serials/a/hls/index.m3u8", "hls")]
    public void DetectsStrmContainer(string url, string container) =>
        Assert.Equal(container, Naming.ContainerFor("x.strm", url));

    [Theory]
    [InlineData("Alpha (2019) [tmdbid-42502]", "Tmdb", "42502")]
    [InlineData("Alpha {tmdb-77} 1080p", "Tmdb", "77")]
    [InlineData("Matrix [imdbid-tt0133093]", "Imdb", "tt0133093")]
    [InlineData("Show [tvdbid-81189]", "Tvdb", "81189")]
    [InlineData("Show [TMDBID=5]", "Tmdb", "5")]
    public void ProviderIds_AreReadFromNames(string name, string key, string value) =>
        Assert.Equal(value, Naming.ParseProviderIds(name)[key]);

    [Fact]
    public void ProviderIds_FirstNameWins_AndPlainNamesHaveNone()
    {
        Assert.Equal("1", Naming.ParseProviderIds("A [tmdbid-1]", "B [tmdbid-2]")["Tmdb"]);
        Assert.Empty(Naming.ParseProviderIds("Alpha (2019) [UKR_ENG] [Hurtom]", null));
    }}
