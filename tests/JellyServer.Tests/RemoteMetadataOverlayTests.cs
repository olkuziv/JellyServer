using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using Xunit;

namespace JellyServer.Tests;

public class RemoteMetadataOverlayTests
{
    private static Item Movie(string name = "Alpha") => new() { Type = ItemTypes.Movie, Name = name, SortName = name.ToLowerInvariant() };

    [Fact]
    public void FillsGaps_AndKeepsLocalValues()
    {
        var item = Movie();
        item.Overview = "From the NFO";
        var remote = new RemoteMetadata { Overview = "From TMDB", Tagline = "Tag", Genres = ["Drama"], CommunityRating = 7.5f, ProductionYear = 2019 };

        Assert.True(RemoteMetadataOverlay.Apply(item, null, remote, replaceAll: false));

        Assert.Equal("From the NFO", item.Overview);
        Assert.Equal("Tag", item.Tagline);
        Assert.Equal(["Drama"], item.Genres);
        Assert.Equal(7.5f, item.CommunityRating);
        Assert.Equal(2019, item.ProductionYear);
    }

    [Fact]
    public void NewerAnswerReplacesThePreviousOne_ButNotLocalValues()
    {
        var first = new RemoteMetadata { Overview = "old", Genres = ["A"], Images = new() { Primary = "https://x/old.jpg" } };
        var item = Movie();
        RemoteMetadataOverlay.Apply(item, null, first, false);
        var second = new RemoteMetadata { Overview = "new", Genres = ["B"], Images = new() { Primary = "https://x/new.jpg" } };

        RemoteMetadataOverlay.Apply(item, first, second, false);

        Assert.Equal("new", item.Overview);
        Assert.Equal(["B"], item.Genres);
        Assert.Equal("https://x/new.jpg", item.Images.Primary);

        item.Overview = "edited by hand"; // not equal to what the provider said: it is local now
        RemoteMetadataOverlay.Apply(item, second, new RemoteMetadata { Overview = "newer" }, false);
        Assert.Equal("edited by hand", item.Overview);
    }

    [Fact]
    public void ReplaceAll_LetsTheProviderWin()
    {
        var item = Movie();
        item.Overview = "local";
        item.Images.Primary = "/media/poster.jpg";
        RemoteMetadataOverlay.Apply(item, null, new RemoteMetadata { Overview = "remote", Images = new() { Primary = "https://x/p.jpg" } }, replaceAll: true);
        Assert.Equal("remote", item.Overview);
        Assert.Equal("https://x/p.jpg", item.Images.Primary);
    }

    [Fact]
    public void LocalPoster_BeatsRemotePoster()
    {
        var item = Movie();
        item.Images.Primary = "/media/Alpha/poster.jpg";
        RemoteMetadataOverlay.Apply(item, null, new RemoteMetadata { Images = new() { Primary = "https://x/p.jpg", Logo = "https://x/l.png" } }, false);
        Assert.Equal("/media/Alpha/poster.jpg", item.Images.Primary);
        Assert.Equal("https://x/l.png", item.Images.Logo);
    }

    [Fact]
    public void Name_FollowsTheProvider_UnlessAnNfoGaveIt()
    {
        var guessed = Movie("naruto");
        RemoteMetadataOverlay.Apply(guessed, null, new RemoteMetadata { Name = "Наруто" }, false);
        Assert.Equal("Наруто", guessed.Name);
        Assert.Equal(ItemNaming.SortName("Наруто"), guessed.SortName);
        Assert.Contains("наруто", guessed.SearchText);

        var locked = Movie("Mine");
        locked.NameLocked = true;
        RemoteMetadataOverlay.Apply(locked, null, new RemoteMetadata { Name = "Other" }, replaceAll: true);
        Assert.Equal("Mine", locked.Name);
    }

    [Fact]
    public void Episodes_KeepTheirNumericSortName()
    {
        var ep = new Item { Type = ItemTypes.Episode, Name = "Episode 1", SortName = "000100001" };
        RemoteMetadataOverlay.Apply(ep, null, new RemoteMetadata { Name = "Pilot" }, false);
        Assert.Equal("Pilot", ep.Name);
        Assert.Equal("000100001", ep.SortName);
    }

    [Fact]
    public void ProviderIds_AreAdded_UserIdsWin()
    {
        var item = Movie();
        item.ProviderIds["Tmdb"] = "1";
        RemoteMetadataOverlay.Apply(item, null, new RemoteMetadata { ProviderIds = { ["Tmdb"] = "2", ["Imdb"] = "tt1" } }, false);
        Assert.Equal("1", item.ProviderIds["Tmdb"]);
        Assert.Equal("tt1", item.ProviderIds["Imdb"]);
    }

    [Fact]
    public void NothingNew_ChangesNothing()
    {
        var item = Movie();
        var remote = new RemoteMetadata { Overview = "x", Genres = ["A"] };
        RemoteMetadataOverlay.Apply(item, null, remote, false);
        Assert.False(RemoteMetadataOverlay.Apply(item, remote, remote, false));
    }

    [Fact]
    public void MergeUnder_FirstProviderWinsAndLaterOnesFillGaps()
    {
        var first = new RemoteMetadata { Overview = "first", Images = new() { Primary = "https://a/1.jpg" } };
        var second = new RemoteMetadata { Overview = "second", Tagline = "t", Genres = ["G"], Images = new() { Primary = "https://b/1.jpg", Logo = "https://b/l.png" } };
        var merged = first.MergeUnder(second);
        Assert.Equal("first", merged.Overview);
        Assert.Equal("t", merged.Tagline);
        Assert.Equal(["G"], merged.Genres);
        Assert.Equal("https://a/1.jpg", merged.Images.Primary);
        Assert.Equal("https://b/l.png", merged.Images.Logo);
    }
}