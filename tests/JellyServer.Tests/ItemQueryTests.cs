using JellyServer.Catalog;
using Xunit;
using JellyServer.Kernel;
using JellyServer.Identity.Contracts;

namespace JellyServer.Tests;

/// <summary>User data lives in another module (maybe another database): the catalog SQL must never reference it.</summary>
public class ItemQueryTests
{
    [Fact]
    public void Build_NeverJoinsUserData()
    {
        var q = new ItemQuery
        {
            UserId = Guid.NewGuid(), IsFavorite = true, IsPlayed = false, IsResumable = true,
            Sort = [("DatePlayed", true), ("SortName", false)],
        };

        var (fromWhere, orderBy, _, _) = q.Build();

        Assert.DoesNotContain("user_data", fromWhere);
        Assert.DoesNotContain("ud.", fromWhere);
        Assert.DoesNotContain("ud.", orderBy);
        Assert.Contains("i.sort_name ASC", orderBy);
    }

    [Theory]
    [InlineData("DatePlayed")]
    [InlineData("playcount")]
    [InlineData("IsFavoriteOrLiked")]
    [InlineData("IsPlayed")]
    [InlineData("IsUnplayed")]
    public void UserDataSortKeys_AreRecognised(string field)
    {
        Assert.True(ItemQuery.IsUserDataSort(field));
        Assert.True(new ItemQuery { Sort = [(field, false)] }.NeedsUserData);
    }

    [Fact]
    public void CatalogOnlyQuery_DoesNotNeedUserData()
    {
        var q = new ItemQuery { Types = ["Movie"], Sort = [("SortName", false), ("DateCreated", true)] };
        Assert.False(q.NeedsUserData);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var q = new ItemQuery { IsFavorite = true, Ids = [Guid.NewGuid()] };
        var c = q.Clone();
        c.IsFavorite = null;
        c.Ids = null;
        Assert.True(q.IsFavorite);
        Assert.NotNull(q.Ids);
    }
}
