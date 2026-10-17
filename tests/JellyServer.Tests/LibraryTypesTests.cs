using JellyServer.Scanning;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

public class LibraryTypesTests
{
    [Theory]
    [InlineData(null, "mixed")]
    [InlineData("", "mixed")]
    [InlineData("mixed", "mixed")]
    [InlineData("Movies", "movies")]
    [InlineData("tvshows", "tvshows")]
    [InlineData("music", null)]
    public void Normalize_MapsClientValues(string? input, string? expected) => Assert.Equal(expected, LibraryTypes.Normalize(input));

    [Fact]
    public void Mixed_HasNoClientCollectionType_AndShowsMoviesAndSeries()
    {
        Assert.Null(LibraryTypes.ForClient("mixed"));
        Assert.Equal("movies", LibraryTypes.ForClient("movies"));
        Assert.Equal([ItemTypes.Movie, ItemTypes.Series], LibraryTypes.TopLevelTypes("mixed"));
        Assert.Equal([ItemTypes.Series], LibraryTypes.TopLevelTypes("tvshows"));
        Assert.Equal([ItemTypes.Movie], LibraryTypes.TopLevelTypes("movies"));
    }

    [Theory]
    [InlineData("Season 1", 1)]
    [InlineData("Specials", 0)]
    [InlineData("S02", 2)]
    [InlineData("12 Angry Men (1957)", null)] // a movie folder, not season 12
    [InlineData("2019", null)]
    public void StrictSeasonDir_IgnoresBareNumbers(string dir, int? expected) =>
        Assert.Equal(expected, Naming.ParseSeasonDir(dir, allowBareNumber: false));

    [Theory]
    [InlineData("Naruto Shippuden S01", true)]
    [InlineData("Naruto Shippuden S02 2007-2008", true)]
    [InlineData("Show - Season 3 [1080p]", true)]
    [InlineData("Season 1", true)]
    [InlineData("12 Angry Men (1957)", false)]
    [InlineData("Seven (1995)", false)]
    [InlineData("Starship Troopers", false)]
    [InlineData("Alpha (2019)", false)]
    public void SeasonMarker_RecognisesNamedSeasonFolders(string dir, bool expected) => Assert.Equal(expected, Naming.HasSeasonMarker(dir));

    [Fact]
    public void LenientSeasonDir_StillAcceptsBareNumbers() => Assert.Equal(3, Naming.ParseSeasonDir("3"));
}
