using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Media.Contracts;
using JellyServer.People.Contracts;
using Xunit;

namespace JellyServer.Tests;

/// <summary>An item is stored in three modules (catalog row, Media files, People credits): taking it apart and back must lose nothing.</summary>
public class ItemPartsTests
{
    private static Item Movie()
    {
        var id = Guid.NewGuid();
        return new Item
        {
            Id = id, Type = ItemTypes.Movie, Name = "Movie", Path = "/media/Movie/Movie.mkv", Container = "mkv", Size = 100, RuntimeTicks = 50,
            Fingerprint = "fp", MediaInfo = new MediaInfo { Video = [new VideoStream("h264", 1920, 1080, 1.78, null)] },
            Subtitles = [new SubtitleFile("/media/Movie/Movie.uk.srt", "srt", "ukr", false)],
            People = [new Person("Actor One", "Hero", "Actor") { ImageUrl = "https://img/1.jpg", ProviderId = "1" }],
            Versions =
            [
                new ItemVersion { Id = Guid.NewGuid(), Path = "/media/Movie/Movie - 720p.mkv", Container = "mkv", Size = 50, Fingerprint = "v1" },
                new ItemVersion { Id = Guid.NewGuid(), Path = "/media/Movie/Movie - dub.strm", StreamUrl = "http://x/1", Container = "strm", Fingerprint = "v2" },
            ],
        };
    }

    [Fact]
    public void Sources_MainFileFirstWithTheItemsId_ThenVersionsInOrder()
    {
        var it = Movie();
        var sources = ItemParts.Sources(it);
        Assert.Equal(3, sources.Count);
        Assert.Equal(it.Id, sources[0].Id);
        Assert.All(sources, s => Assert.Equal(it.Id, s.ItemId));
        Assert.Equal([0, 1, 2], sources.Select(s => s.Ordinal));
        Assert.Equal(it.Versions![1].StreamUrl, sources[2].StreamUrl);
        Assert.Same(it.MediaInfo, sources[0].MediaInfo);
    }

    [Fact]
    public void Folders_HaveNoFiles() =>
        Assert.Empty(ItemParts.Sources(new Item { Id = Guid.NewGuid(), Type = ItemTypes.Series, Path = "/media/Show" }));

    [Fact]
    public void Attach_PutsFilesAndCreditsBack()
    {
        var original = Movie();
        var row = new Item { Id = original.Id, Type = ItemTypes.Movie, Name = original.Name, Path = original.Path, Size = original.Size, Fingerprint = "fp" };
        var sources = new Dictionary<Guid, List<MediaSource>> { [original.Id] = ItemParts.Sources(original) };
        var credits = new Dictionary<Guid, List<Person>> { [original.Id] = original.People! };

        ItemParts.Attach([row], sources, credits);

        Assert.Equal("mkv", row.Container);
        Assert.Equal(1920, row.MediaInfo!.Video[0].Width);
        Assert.Single(row.Subtitles!);
        Assert.Equal(original.Versions!.Select(v => v.Id), row.Versions!.Select(v => v.Id));
        Assert.Equal("http://x/1", row.Versions[1].StreamUrl);
        Assert.Equal("Hero", row.People![0].Role);
        // A version id still finds the item and plays the version's file.
        var view = row.AsVersion(row.Versions[0]);
        Assert.Equal(original.Versions[0].Path, view.Path);
        Assert.Equal(row.Id, view.PrimaryId);
    }

    [Fact]
    public void Attach_ItemWithoutFilesOrCredits_StaysEmpty()
    {
        var row = new Item { Id = Guid.NewGuid(), Type = ItemTypes.Movie, Path = "/m.mkv" };
        ItemParts.Attach([row], [], []);
        Assert.Null(row.People);
        Assert.Null(row.Versions);
        Assert.Null(row.MediaInfo);
    }

    [Fact]
    public void PersonKinds_FollowJellyfinsEnum()
    {
        Assert.Equal(1, PersonKinds.Code("Actor"));
        Assert.Equal(5, PersonKinds.Code("GuestStar"));
        Assert.Equal(13, PersonKinds.Code("creator"));
        Assert.Equal(0, PersonKinds.Code("Somebody"));
        Assert.Equal("Director", PersonKinds.Name(2));
        Assert.Equal("Unknown", PersonKinds.Name(99));
    }

    [Fact]
    public void PersonIds_AreTheIdsClientsAlreadyKnow() =>
        Assert.Equal(Kernel.Ids.FromKey("person", "Actor One"), PersonIds.Of("Actor One"));

    [Fact]
    public void Playlists_AreVisibleToOwnerSharesOrEveryoneWhenPublic()
    {
        var owner = Guid.NewGuid();
        var friend = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var playlist = new Collection { Kind = CollectionKinds.Playlist, OwnerId = owner, Shares = [new CollectionShare(friend, CanEdit: false)] };

        Assert.True(playlist.IsVisibleTo(owner));
        Assert.True(playlist.IsVisibleTo(friend));
        Assert.False(playlist.IsVisibleTo(stranger));
        Assert.True(playlist.CanEdit(owner));
        Assert.False(playlist.CanEdit(friend));

        playlist.OpenAccess = true;
        Assert.True(playlist.IsVisibleTo(stranger));
        Assert.False(playlist.CanEdit(stranger));

        Assert.True(new Collection { Kind = CollectionKinds.BoxSet }.IsVisibleTo(stranger));
    }
}
