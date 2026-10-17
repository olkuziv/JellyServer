using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using JellyServer.Catalog.Contracts;
using JellyServer.Kernel.Events;
using JellyServer.Metadata;
using JellyServer.Metadata.Contracts;
using JellyServer.Settings.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JellyServer.Tests;

public class MetadataEngineTests
{
    // ---- fakes ----

    private sealed class FakeProvider(string name, Func<MetadataRequest, ProviderResult>? movie = null, Func<SeasonRequest, SeasonProviderResult>? season = null) : IMetadataProvider
    {
        public string Name { get; } = name;
        public bool IsAvailable { get; set; } = true;
        public IReadOnlyCollection<string> ItemTypes { get; } = [Catalog.Contracts.ItemTypes.Movie, Catalog.Contracts.ItemTypes.Series, Catalog.Contracts.ItemTypes.Season, Catalog.Contracts.ItemTypes.Episode];
        public IReadOnlyCollection<string> ImageTypes { get; } = ["Primary", "Backdrop", "Logo"];
        public ConcurrentQueue<MetadataRequest> Requests { get; } = new();
        public ConcurrentQueue<SeasonRequest> SeasonRequests { get; } = new();

        public Task<ProviderResult> FetchAsync(MetadataRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(movie?.Invoke(request) ?? ProviderResult.NotFound);
        }

        public Task<SeasonProviderResult> FetchSeasonAsync(SeasonRequest request, CancellationToken ct)
        {
            SeasonRequests.Enqueue(request);
            return Task.FromResult(season?.Invoke(request) ?? SeasonProviderResult.NotFound);
        }
    }

    private sealed class Refs(params ItemRef[] items) : IItemRefs
    {
        public Task<List<ItemRef>> PageAsync(Guid? afterId, int limit, CancellationToken ct = default) =>
            Task.FromResult(items.OrderBy(i => i.Id).Where(i => afterId is null || i.Id.CompareTo(afterId.Value) > 0).Take(limit).ToList());

        public Task<List<ItemRef>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult(items.Where(i => ids.Contains(i.Id)).ToList());

        public Task<List<ItemRef>> GetDescendantsAsync(Guid itemId, CancellationToken ct = default) =>
            Task.FromResult(items.Where(i => i.SeriesId == itemId).ToList());
    }

    private sealed class Libraries(params Library[] all) : ILibraryDirectory
    {
        public IReadOnlyList<Library> All { get; } = all;
        public Library? Find(Guid id) => All.FirstOrDefault(l => l.Id == id);
    }

    private sealed class Sink : IRemoteMetadataSink
    {
        public ConcurrentQueue<RemoteMetadataUpdate> Updates { get; } = new();
        public Action? OnApply { get; set; }
        public Task<int> ApplyAsync(IReadOnlyList<RemoteMetadataUpdate> updates, CancellationToken ct = default)
        {
            OnApply?.Invoke();
            foreach (var u in updates) Updates.Enqueue(u);
            return Task.FromResult(updates.Count);
        }
    }

    private sealed class Store : IMetadataStore
    {
        public ConcurrentDictionary<Guid, string> States { get; } = new();
        public Task<HashSet<Guid>> DueAsync(IReadOnlyCollection<Guid> ids, TimeSpan notFoundRetry, TimeSpan failedRetry, CancellationToken ct) =>
            Task.FromResult(ids.Where(i => !States.ContainsKey(i)).ToHashSet());
        public Task SaveAsync(IReadOnlyCollection<(Guid Id, string Status)> states, CancellationToken ct)
        {
            foreach (var (id, status) in states) States[id] = status;
            return Task.CompletedTask;
        }
        public ConcurrentDictionary<Guid, RemoteMetadata> Remote { get; } = new();
        public List<string> Log { get; } = [];
        public Task<Dictionary<Guid, RemoteMetadata>> LoadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult(ids.Where(Remote.ContainsKey).ToDictionary(i => i, i => Remote[i]));
        public Task SaveRemoteAsync(IReadOnlyCollection<(Guid Id, RemoteMetadata Data)> answers, CancellationToken ct)
        {
            lock (Log) Log.Add("store");
            foreach (var (id, data) in answers) Remote[id] = data;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        {
            foreach (var id in ids) Remote.TryRemove(id, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class Config : IServerConfiguration
    {
        public Task<JsonObject> GetAsync(CancellationToken ct = default) => Task.FromResult(new JsonObject { ["PreferredMetadataLanguage"] = "uk", ["MetadataCountryCode"] = "UA" });
        public Task<bool> IsStartupCompletedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UpdateAsync(Action<JsonObject> edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAsync(JsonElement body, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteStartupAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ServerNameAsync(CancellationToken ct = default) => Task.FromResult<string?>("test");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Sink Sink { get; } = new();
        public Store Store { get; } = new();
        public EventBus Events { get; } = new(NullLogger<EventBus>.Instance);
        public MetadataEngine Engine { get; }
        public Guid LibraryId { get; } = Guid.NewGuid();

        public ItemRef[] Items { get; }

        public Rig(string libraryOptions, Func<Guid, ItemRef[]> items, params IMetadataProvider[] providers)
        {
            Items = items(LibraryId);
            var library = new Library { Id = LibraryId, Name = "Test", CollectionType = "mixed", OptionsJson = libraryOptions };
            Engine = new MetadataEngine(new Refs(Items), new Libraries(library), providers, Sink, Store, new Config(), Events,
                Options.Create(new MetadataOptions { Concurrency = 2 }), new ServiceCollection().BuildServiceProvider(), NullLogger<MetadataEngine>.Instance);
        }

        public Task StartAsync() => Engine.StartAsync(CancellationToken.None);

        public async Task<bool> WaitForAsync(Func<bool> condition, int milliseconds = 5000)
        {
            for (var waited = 0; waited < milliseconds; waited += 20)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return condition();
        }

        public async ValueTask DisposeAsync() => await Engine.StopAsync(CancellationToken.None);
    }

    private static ItemRef Item(Guid lib, string type, string name, Guid? id = null, Guid? series = null, int? parentIndex = null, int? index = null, params (string, string)[] ids) =>
        new(id ?? Guid.NewGuid(), type, name, null, lib, series, parentIndex, index, ids.ToDictionary(i => i.Item1, i => i.Item2));

    private static RemoteMetadata Md(string overview, string? tagline = null, string? poster = null) =>
        new() { Overview = overview, Tagline = tagline, Images = new ItemImages { Primary = poster } };

    // ---- movies ----

    private static Rig MovieRig(string options, FakeProvider[] providers, (string, string)[]? ids = null) =>
        new(options, lib => [Item(lib, ItemTypes.Movie, "Alpha", ids: ids ?? [])], providers);

    [Fact]
    public async Task ScanCompleted_LooksTheMovieUp_AndHandsItToTheCatalog()
    {
        var provider = new FakeProvider("TheMovieDb", movie: r => new(ProviderStatus.Found, Md("Опис", poster: "https://x/p.jpg")));
        await using var rig = MovieRig("{}", [provider], [("Tmdb", "5")]);
        var movie = rig.Items[0];
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [movie.Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1 && rig.Store.States.Count == 1));
        var update = Assert.Single(rig.Sink.Updates);
        Assert.Equal(movie.Id, update.ItemId);
        Assert.Equal("Опис", update.Metadata.Overview);
        Assert.Equal("https://x/p.jpg", update.Metadata.Images.Primary);
        Assert.False(update.ReplaceAll);
        Assert.Equal(ItemStatus.Found, rig.Store.States[movie.Id]);
        var request = Assert.Single(provider.Requests);
        Assert.Equal(("uk", "UA"), (request.Context.Language, request.Context.Country)); // the server's defaults
        Assert.Equal("5", request.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task TheAnswer_IsStoredByTheMetadataModule_BeforeTheCatalogLaysItOver()
    {
        var provider = new FakeProvider("TheMovieDb", movie: r => new(ProviderStatus.Found, Md("Опис")));
        await using var rig = MovieRig("{}", [provider]);
        var movie = rig.Items[0];
        var storedWhenApplied = false;
        rig.Sink.OnApply = () => storedWhenApplied = rig.Store.Remote.ContainsKey(movie.Id);
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [movie.Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1));
        Assert.True(storedWhenApplied);   // a crash after the catalog step can never lose the answer
        Assert.Equal("Опис", rig.Store.Remote[movie.Id].Overview);
    }

    [Fact]
    public async Task WhatWasStoredBefore_TravelsWithTheUpdate()
    {
        var provider = new FakeProvider("TheMovieDb", movie: r => new(ProviderStatus.Found, Md("new")));
        await using var rig = MovieRig("{}", [provider]);
        var movie = rig.Items[0];
        rig.Store.Remote[movie.Id] = Md("old");
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [movie.Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1));
        var update = Assert.Single(rig.Sink.Updates);
        Assert.Equal("old", update.Previous?.Overview);
        Assert.Equal("new", update.Metadata.Overview);
        Assert.Equal("new", rig.Store.Remote[movie.Id].Overview);
    }

    [Fact]
    public async Task UnknownItem_IsRemembered_AndNothingIsWritten()
    {
        var provider = new FakeProvider("TheMovieDb");
        await using var rig = MovieRig("{}", [provider]);
        var movie = rig.Items[0];
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [movie.Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Store.States.Count == 1));
        Assert.Equal(ItemStatus.NotFound, rig.Store.States[movie.Id]);
        Assert.Empty(rig.Sink.Updates);
    }

    [Fact]
    public async Task Providers_FollowTheLibrarysOrder_FirstWinsNextFillsGaps()
    {
        var a = new FakeProvider("A", movie: _ => new(ProviderStatus.Found, Md("from A", poster: "https://a/p.jpg")));
        var b = new FakeProvider("B", movie: _ => new(ProviderStatus.Found, Md("from B", tagline: "tag B", poster: "https://b/p.jpg")));
        var options = """{"TypeOptions":[{"Type":"Movie","MetadataFetchers":["A","B"],"MetadataFetcherOrder":["B","A"],"ImageFetchers":["A","B"],"ImageFetcherOrder":["B","A"]}]}""";
        await using var rig = MovieRig(options, [a, b]);
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [rig.Items[0].Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1));
        var md = rig.Sink.Updates.Single().Metadata;
        Assert.Equal("from B", md.Overview);          // B is first in the library's order
        Assert.Equal("tag B", md.Tagline);
        Assert.Equal("https://b/p.jpg", md.Images.Primary);
        Assert.Equal(2, a.Requests.Count + b.Requests.Count); // both were asked
    }

    [Fact]
    public async Task ADisabledProvider_IsNeverAsked()
    {
        var provider = new FakeProvider("TheMovieDb", movie: _ => new(ProviderStatus.Found, Md("x")));
        await using var rig = MovieRig("""{"TypeOptions":[{"Type":"Movie","MetadataFetchers":[],"ImageFetchers":[]}]}""", [provider]);
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [rig.Items[0].Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Store.States.Count == 1));
        Assert.Equal(ItemStatus.Skipped, rig.Store.States[rig.Items[0].Id]);
        Assert.Empty(provider.Requests);
        Assert.Empty(rig.Sink.Updates);
    }

    [Fact]
    public async Task OnlyImagesFromAProvider_WhenMetadataIsOffForIt()
    {
        var provider = new FakeProvider("TheMovieDb", movie: _ => new(ProviderStatus.Found, Md("text", poster: "https://x/p.jpg")));
        await using var rig = MovieRig("""{"TypeOptions":[{"Type":"Movie","MetadataFetchers":[],"ImageFetchers":["TheMovieDb"]}]}""", [provider]);
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [rig.Items[0].Id]));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1));
        var md = rig.Sink.Updates.Single().Metadata;
        Assert.Null(md.Overview);
        Assert.Equal("https://x/p.jpg", md.Images.Primary);
    }

    [Fact]
    public async Task AlreadyLookedUpItems_AreNotAskedAgain_ButARefreshAsksAndReplaces()
    {
        var provider = new FakeProvider("TheMovieDb", movie: _ => new(ProviderStatus.Found, Md("x")));
        await using var rig = MovieRig("{}", [provider]);
        var movie = rig.Items[0];
        rig.Store.States[movie.Id] = ItemStatus.Found;
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [movie.Id]));
        await Task.Delay(200);
        Assert.Empty(provider.Requests);

        await rig.Engine.RefreshAsync(new MetadataRefreshRequest(movie.Id, Recursive: true, ReplaceMetadata: true, ReplaceImages: false));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1));
        var update = rig.Sink.Updates.Single();
        Assert.True(update.ReplaceAll);
        Assert.False(update.ReplaceImages);
    }

    [Fact]
    public async Task ProviderWithoutAKey_IsIgnored()
    {
        var provider = new FakeProvider("TheMovieDb", movie: _ => new(ProviderStatus.Found, Md("x"))) { IsAvailable = false };
        await using var rig = MovieRig("{}", [provider]);
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, [rig.Items[0].Id]));
        await Task.Delay(200);

        Assert.Empty(provider.Requests);
        Assert.Empty(rig.Store.States); // nothing is recorded, so the item is looked up once a key appears
    }

    // ---- series ----

    [Fact]
    public async Task Series_IsLookedUpFirst_ThenItsSeasonsWithTheFoundId()
    {
        var provider = new FakeProvider("TheMovieDb",
            movie: r => new(ProviderStatus.Found, new RemoteMetadata { Overview = "series", ProviderIds = { ["Tmdb"] = "1429" } }),
            season: r => new(ProviderStatus.Found, new RemoteMetadata { Name = "Сезон 1" },
                new Dictionary<int, RemoteMetadata> { [1] = new() { Name = "Ep 1" }, [2] = new() { Name = "Ep 2" } }));
        await using var rig = new Rig("{}", lib =>
        {
            var series = Item(lib, ItemTypes.Series, "AoT");
            return
            [
                series,
                Item(lib, ItemTypes.Season, "Season 1", series: series.Id, index: 1),
                Item(lib, ItemTypes.Episode, "Episode 1", series: series.Id, parentIndex: 1, index: 1),
                Item(lib, ItemTypes.Episode, "Episode 2", series: series.Id, parentIndex: 1, index: 2),
                Item(lib, ItemTypes.Episode, "Episode 3", series: series.Id, parentIndex: 1, index: 3), // the provider does not know it
            ];
        }, provider);
        var (series, season, ep1, ep2, ep3) = (rig.Items[0], rig.Items[1], rig.Items[2], rig.Items[3], rig.Items[4]);
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, rig.Items.Select(i => i.Id).ToList()));

        Assert.True(await rig.WaitForAsync(() => rig.Store.States.Count == 5));
        Assert.Equal(ItemStatus.Found, rig.Store.States[series.Id]);
        Assert.Equal(ItemStatus.Found, rig.Store.States[season.Id]);
        Assert.Equal(ItemStatus.Found, rig.Store.States[ep1.Id]);
        Assert.Equal(ItemStatus.NotFound, rig.Store.States[ep3.Id]);

        var seasonRequest = Assert.Single(provider.SeasonRequests);
        Assert.Equal(1, seasonRequest.SeasonNumber);
        Assert.Equal("1429", seasonRequest.SeriesProviderIds["Tmdb"]); // the id found for the series reaches the season lookup

        var byId = rig.Sink.Updates.ToDictionary(u => u.ItemId, u => u.Metadata);
        Assert.Equal("series", byId[series.Id].Overview);
        Assert.Equal("Сезон 1", byId[season.Id].Name);
        Assert.Equal("Ep 1", byId[ep1.Id].Name);
        Assert.Equal("Ep 2", byId[ep2.Id].Name);
        Assert.False(byId.ContainsKey(ep3.Id));
    }

    [Fact]
    public async Task NewEpisodes_OfAKnownSeries_ReuseTheSeriesIdWithoutAskingForTheSeries()
    {
        var provider = new FakeProvider("TheMovieDb",
            movie: _ => throw new InvalidOperationException("the series must not be fetched again"),
            season: _ => new(ProviderStatus.Found, null, new Dictionary<int, RemoteMetadata> { [4] = new() { Name = "Ep 4" } }));
        await using var rig = new Rig("{}", lib =>
        {
            var series = Item(lib, ItemTypes.Series, "AoT", ids: ("Tmdb", "1429"));
            return [series, Item(lib, ItemTypes.Episode, "Episode 4", series: series.Id, parentIndex: 1, index: 4)];
        }, provider);
        rig.Store.States[rig.Items[0].Id] = ItemStatus.Found;
        await rig.StartAsync();

        await rig.Events.PublishAsync(new ScanCompleted(rig.LibraryId, rig.Items.Select(i => i.Id).ToList()));

        Assert.True(await rig.WaitForAsync(() => rig.Sink.Updates.Count == 1));
        Assert.Empty(provider.Requests);
        Assert.Equal("1429", provider.SeasonRequests.Single().SeriesProviderIds["Tmdb"]);
        Assert.Equal("Ep 4", rig.Sink.Updates.Single().Metadata.Name);
    }
    // ---- planning ----

    [Fact]
    public void Planner_UsesTheLibrarysLanguageAndCountry_OverTheServers()
    {
        var provider = new FakeProvider("TheMovieDb");
        var library = new Library { OptionsJson = """{"PreferredMetadataLanguage":"de","MetadataCountryCode":"DE"}""" };
        var plan = MetadataPlanner.Create(library, ItemTypes.Movie, [provider], "uk", "UA");
        Assert.Equal(("de", "DE"), (plan.Context.Language, plan.Context.Country));

        var defaults = MetadataPlanner.Create(new Library { OptionsJson = "{}" }, ItemTypes.Movie, [provider], "uk", "UA");
        Assert.Equal(("uk", "UA"), (defaults.Context.Language, defaults.Context.Country));
    }

    [Fact]
    public void Planner_ReadsImageLimits_AndDefaultsLikeJellyfin()
    {
        var provider = new FakeProvider("TheMovieDb");
        var custom = new Library { OptionsJson = """{"TypeOptions":[{"Type":"Series","ImageOptions":[{"Type":"Backdrop","Limit":3},{"Type":"Logo","Limit":0}]}]}""" };
        var plan = MetadataPlanner.Create(custom, ItemTypes.Series, [provider], "en", "US");
        Assert.Equal(3, plan.BackdropLimit);
        Assert.Equal(0, plan.ImageLimits["Logo"]);
        Assert.Equal(1, plan.ImageLimits["Primary"]);

        Assert.Equal(1, MetadataPlanner.Create(new Library { OptionsJson = "{}" }, ItemTypes.Movie, [provider], "en", "US").BackdropLimit);
        Assert.Equal(0, MetadataPlanner.Create(new Library { OptionsJson = "{}" }, ItemTypes.Episode, [provider], "en", "US").BackdropLimit);
    }

    [Fact]
    public void Restrict_AppliesTheLimits()
    {
        var provider = new FakeProvider("P");
        var plan = MetadataPlanner.Create(new Library { OptionsJson = """{"TypeOptions":[{"Type":"Movie","ImageOptions":[{"Type":"Backdrop","Limit":2},{"Type":"Logo","Limit":0}]}]}""" }, ItemTypes.Movie, [provider], "en", "US");
        var md = new RemoteMetadata { Images = new ItemImages { Primary = "p", Logo = "l", Backdrops = ["1", "2", "3"] } };
        var restricted = MetadataEngine.Restrict(md, new ProviderPick(provider, true, true), plan);
        Assert.Equal(["1", "2"], restricted.Images.Backdrops);
        Assert.Null(restricted.Images.Logo);
        Assert.Equal("p", restricted.Images.Primary);
    }
}