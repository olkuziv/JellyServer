using System.Text.Json;
using JellyServer.Api.Endpoints;
using JellyServer.Kernel.Events;
using JellyServer.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

public class SetupTests
{
    [Theory]
    [InlineData("admin", true)]
    [InlineData("Ann Lee", true)]
    [InlineData("o'brien_x-1.2@host+tag", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("two  spaces", false)]
    [InlineData("bad/name", false)]
    public void UserNameRule_MatchesJellyfin(string name, bool valid) => Assert.Equal(valid, StartupEndpoints.IsValidUserName(name));

    [Fact]
    public void LibraryOptions_Create_AppliesDefaultsAndClientValues()
    {
        using var sent = JsonDocument.Parse("""{"EnableRealtimeMonitor":true,"SeasonZeroDisplayName":"Extras","PathInfos":[{"Path":"/x"}]}""");
        using var doc = JsonDocument.Parse(LibraryOptionsJson.Create(sent.RootElement));
        var o = doc.RootElement;

        Assert.True(o.GetProperty("EnableRealtimeMonitor").GetBoolean());
        Assert.Equal("Extras", o.GetProperty("SeasonZeroDisplayName").GetString());
        Assert.True(o.GetProperty("EnablePhotos").GetBoolean()); // Jellyfin default survives
        Assert.True(o.GetProperty("SaveSubtitlesWithMedia").GetBoolean());
        Assert.False(o.TryGetProperty("PathInfos", out _)); // paths are stored separately
    }

    [Fact]
    public void LibraryOptions_WithPaths_BuildsPathInfos()
    {
        var o = LibraryOptionsJson.WithPaths("{}", ["/media/a", "/media/b"]);
        var infos = o["PathInfos"]!.AsArray();
        Assert.Equal(2, infos.Count);
        Assert.Equal("/media/b", infos[1]!["Path"]!.GetValue<string>());
        Assert.NotNull(o["EnablePhotos"]); // empty stored options fall back to the defaults
    }

    [Fact]
    public void LibraryOptions_PathsOf_ReadsPathInfos()
    {
        using var doc = JsonDocument.Parse("""{"PathInfos":[{"Path":"/a"},{"Path":""},{"Path":"/b"}]}""");
        Assert.Equal(["/a", "/b"], LibraryOptionsJson.PathsOf(doc.RootElement));
    }

    [Fact]
    public async Task EventBus_DeliversToAllSubscribers_AndSurvivesFailures()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var received = new List<string>();
        bus.Subscribe<LibraryRemoved>((_, _) => throw new InvalidOperationException("boom"));
        using var second = bus.Subscribe<LibraryRemoved>((e, _) =>
        {
            received.Add("second:" + e.ItemIds.Count);
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new LibraryRemoved(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]));
        second.Dispose();
        await bus.PublishAsync(new LibraryRemoved(Guid.NewGuid(), []));

        Assert.Equal(["second:2"], received);
    }
}
