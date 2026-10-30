using JellyServer.Scanning;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.People.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Library browsing: views, item queries, latest, resume, next up, shows, search, genres.</summary>
public static class ItemEndpoints
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
        { ItemTypes.Movie, ItemTypes.Series, ItemTypes.Season, ItemTypes.Episode };

    private static readonly string[] FolderTypes = [ItemTypes.Series, ItemTypes.Season];

    /// <summary>Box sets are "pre-sorted" in Jellyfin: their display order (premiere date) wins over what the client asks.</summary>
    private static readonly List<(string, bool)> BoxSetOrder = [("PremiereDate", false), ("ProductionYear", false), ("SortName", false)];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("").RequireToken();
        g.MapGet("/UserViews", Views);
        g.MapGet("/Users/{userId}/Views", Views);
        g.MapGet("/UserViews/GroupingOptions", () => SystemEndpoints.EmptyList());
        g.MapGet("/Users/{userId}/GroupingOptions", () => SystemEndpoints.EmptyList());
        g.MapGet("/Library/MediaFolders", MediaFolders);

        g.MapGet("/Items", GetItems);
        g.MapGet("/Users/{userId}/Items", GetItems);
        g.MapGet("/Items/Latest", Latest);
        g.MapGet("/Users/{userId}/Items/Latest", Latest);
        g.MapGet("/UserItems/Resume", Resume);
        g.MapGet("/Users/{userId}/Items/Resume", Resume);
        g.MapGet("/Items/Counts", Counts);
        g.MapGet("/Items/Filters", Filters);
        g.MapGet("/Items/Filters2", Filters2);
        g.MapGet("/Items/{id}", GetItem);
        g.MapGet("/Users/{userId}/Items/{id}", GetItem);
        g.MapGet("/Items/{id}/Ancestors", Ancestors);
        g.MapGet("/Items/{id}/Similar", Similar);
        g.MapGet("/Movies/{id}/Similar", Similar);
        g.MapGet("/Shows/{id}/Similar", Similar);
        g.MapGet("/Items/{id}/Intros", () => SystemEndpoints.EmptyResult());
        g.MapGet("/Users/{userId}/Items/{id}/Intros", () => SystemEndpoints.EmptyResult());
        g.MapGet("/Items/{id}/LocalTrailers", () => SystemEndpoints.EmptyList());
        g.MapGet("/Users/{userId}/Items/{id}/LocalTrailers", () => SystemEndpoints.EmptyList());
        g.MapGet("/Items/{id}/SpecialFeatures", () => SystemEndpoints.EmptyList());
        g.MapGet("/Users/{userId}/Items/{id}/SpecialFeatures", () => SystemEndpoints.EmptyList());
        // Stacked multi-part files are not modelled: every item is a single part.
        g.MapGet("/Videos/{id}/AdditionalParts", () => SystemEndpoints.EmptyResult());
        g.MapGet("/Items/{id}/RemoteImages/Providers", () => SystemEndpoints.EmptyList());
        g.MapGet("/Items/{id}/ThemeMedia", () => Results.Json(new ThemeMediaResult(), AppJson.Default.ThemeMediaResult));
        g.MapGet("/Items/{id}/ExternalIdInfos", () => SystemEndpoints.EmptyList());
        g.MapGet("/Shows/NextUp", NextUp);
        g.MapGet("/Shows/{id}/Seasons", Seasons);
        g.MapGet("/Shows/{id}/Episodes", Episodes);
        g.MapGet("/Search/Hints", SearchHints);
        g.MapGet("/Genres", Genres);
        g.MapGet("/Genres/{name}", Genre);
    }

    private static IResult Page(List<BaseItemDto> items, int total, int start) =>
        Results.Json(new QueryResult<BaseItemDto> { Items = items, TotalRecordCount = total, StartIndex = start }, AppJson.Default.QueryResultBaseItemDto);

    // ---- views ----

    /// <summary>The libraries, then "Collections" once a box set exists and "Playlists" once the user can see a playlist (as Jellyfin).</summary>
    private static async Task<IResult> Views(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ICollectionManager collections,
        CollectionListing listing)
    {
        var me = ctx.Auth().User.Id;
        var restricted = await ctx.RestrictionsAsync();
        var counts = await items.LibraryChildCountsAsync(me, ctx.RequestAborted);
        var views = libs.All.Where(l => restricted.AllowsLibrary(l.Id)).Select(l => dto.LibraryDto(l, counts.GetValueOrDefault(l.Id))).ToList();
        if ((await listing.VisibleAsync(me, await collections.ListAsync(CollectionKinds.BoxSet, me, ctx.RequestAborted), ctx.RequestAborted)) is { Count: > 0 } boxSets)
            views.Add(WithCount(dto.BoxSetsViewDto(), boxSets.Count));
        if ((await collections.ListAsync(CollectionKinds.Playlist, me, ctx.RequestAborted)) is { Count: > 0 } playlists)
            views.Add(WithCount(dto.PlaylistsViewDto(), playlists.Count));
        return Page(views, views.Count, 0);
    }

    private static BaseItemDto WithCount(BaseItemDto view, int count)
    {
        view.ChildCount = count;
        return view;
    }

    /// <summary>Library folders: the libraries and the "Collections" library (the playlists folder is a hidden one in Jellyfin).</summary>
    private static async Task<IResult> MediaFolders(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ICollectionManager collections,
        CollectionListing listing)
    {
        var restricted = await ctx.RestrictionsAsync();
        var counts = await items.LibraryChildCountsAsync(ctx.Auth().User.Id, ctx.RequestAborted);
        var folders = libs.All.Where(l => restricted.AllowsLibrary(l.Id)).Select(l => dto.LibraryDto(l, counts.GetValueOrDefault(l.Id))).ToList();
        if ((await listing.VisibleAsync(ctx.Auth().User.Id, await collections.ListAsync(CollectionKinds.BoxSet, ctx.Auth().User.Id, ctx.RequestAborted), ctx.RequestAborted)) is { Count: > 0 } boxSets)
            folders.Add(WithCount(dto.BoxSetsViewDto(), boxSets.Count));
        return Page(folders, folders.Count, 0);
    }

    // ---- generic item query ----

    /// <summary>Maps Jellyfin query parameters onto <see cref="ItemQuery"/>. Returns null when nothing can match.</summary>
    private static async Task<ItemQuery?> BuildQueryAsync(HttpContext ctx, LibraryRegistry libs, ItemRepository items, ServerIdentity identity,
        ICollectionManager collections, IPeopleStore people)
    {
        var q = ctx.Request.Query;
        var ct = ctx.RequestAborted;
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id,
            StartIndex = q.Int("startIndex") ?? 0,
            Limit = q.Int("limit"),
            IncludeTotal = q.Bool("enableTotalRecordCount") ?? true,
        };
        var recursive = q.Bool("recursive") ?? false;

        // Ids allowed by box set membership, people and the ids parameter: each narrows what the others allowed.
        HashSet<Guid>? only = null;
        void Narrow(IEnumerable<Guid> ids)
        {
            if (only is null) only = [.. ids];
            else only.IntersectWith(ids);
        }

        var requestedTypes = q.List("includeItemTypes");
        var types = requestedTypes.Where(SupportedTypes.Contains)
            .Select(t => SupportedTypes.First(s => s.Equals(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (requestedTypes.Length > 0 && types.Length == 0) return null; // e.g. MusicAlbum (playlists and box sets are answered elsewhere)
        var mediaTypes = q.List("mediaTypes");
        if (types.Length == 0 && mediaTypes.Length > 0)
        {
            if (!mediaTypes.Contains("Video", StringComparer.OrdinalIgnoreCase)) return null;
            types = [ItemTypes.Movie, ItemTypes.Episode];
        }
        var exclude = q.List("excludeItemTypes").Where(SupportedTypes.Contains).ToList();
        var presorted = false;

        if (q.Guid("parentId") is { } parentId && parentId != identity.RootFolderId)
        {
            if (libs.Find(parentId) is { } lib)
            {
                query.LibraryIds = [lib.Id];
                if (!recursive && types.Length == 0)
                    types = LibraryTypes.TopLevelTypes(lib.CollectionType);
            }
            else if (await items.GetAsync(parentId, ct) is { } parent)
            {
                switch (parent.Type)
                {
                    case ItemTypes.Series when recursive: query.SeriesId = parent.Id; break;
                    case ItemTypes.Series: query.ParentId = parent.Id; break;
                    case ItemTypes.Season: query.SeasonId = parent.Id; break;
                    default: return null; // leaf items have no children
                }
            }
            else if (await collections.GetAsync(parentId, ct) is { IsPlaylist: false } boxSet)
            {
                // Its items; recursively, what plays (a series in it stands for its episodes).
                var members = await collections.EntriesAsync(boxSet.Id, ct);
                if (recursive) members = await collections.ExpandAsync(members, ct);
                Narrow(members);
                presorted = true;
            }
            else
            {
                return null;
            }
        }
        else if (!recursive && types.Length == 0 && q.List("ids").Length == 0 && q.List("personIds").Length == 0 && q.Str("person") is null)
        {
            types = [ItemTypes.Movie, ItemTypes.Series];
        }

        // Folders: Jellyfin's isFolder parameter and the IsFolder/IsNotFolder filters.
        var filters = q.List("filters");
        var wantFolders = q.Bool("isFolder") ?? (filters.Contains("IsFolder", StringComparer.OrdinalIgnoreCase) ? true
            : filters.Contains("IsNotFolder", StringComparer.OrdinalIgnoreCase) ? false : null);
        if (wantFolders == false) exclude.AddRange(FolderTypes);
        if (wantFolders == true)
        {
            types = types.Length == 0 ? FolderTypes : [.. types.Intersect(FolderTypes)];
            if (types.Length == 0) return null;
        }

        // People: their credits are kept by the People module.
        var personIds = q.Guids("personIds").ToList();
        if (q.Str("person") is { } personName) personIds.Add(PersonIds.Of(personName));
        if (personIds.Count > 0)
            Narrow(await people.ItemsWithAsync(personIds, q.List("personTypes") is { Length: > 0 } kinds ? kinds : null, ct));

        query.Types = types.Length > 0 ? types : null;
        query.ExcludeTypes = exclude.Count > 0 ? [.. exclude.Distinct()] : null;
        if (q.Guid("seriesId") is { } seriesId) query.SeriesId = seriesId;
        if (q.Guid("seasonId") is { } seasonId) query.SeasonId = seasonId;
        if (q.Guids("ids") is { Length: > 0 } ids) Narrow(ids);
        if (only is not null)
        {
            if (only.Count == 0) return null;
            query.Ids = [.. only];
        }
        if (q.Guids("excludeItemIds") is { Length: > 0 } ex) query.ExcludeIds = ex;
        if (q.Str("searchTerm") is { } term) query.SearchTerm = ItemNaming.SearchText(term);
        query.NameStartsWith = q.Str("nameStartsWith");
        query.NameLessThan = q.Str("nameLessThan");
        query.NameStartsWithOrGreater = q.Str("nameStartsWithOrGreater");
        if (q.List("genres") is { Length: > 0 } genres) query.Genres = genres;
        if (q.Guids("genreIds") is { Length: > 0 } genreIds) query.GenreIds = genreIds;
        if (q.List("years").Select(y => int.TryParse(y, out var n) ? n : 0).Where(n => n > 0).ToArray() is { Length: > 0 } years) query.Years = years;
        if (q.List("officialRatings") is { Length: > 0 } ratings) query.OfficialRatings = ratings;
        query.ParentIndexNumber = q.Int("parentIndexNumber");
        query.IndexNumber = q.Int("indexNumber");
        query.MinCommunityRating = q.Double("minCommunityRating");
        query.IsFavorite = q.Bool("isFavorite");
        query.IsPlayed = q.Bool("isPlayed");
        foreach (var filter in q.List("filters"))
        {
            switch (filter.ToLowerInvariant())
            {
                case "isfavorite" or "isfavoriteorlikes": query.IsFavorite = true; break;
                case "isplayed": query.IsPlayed = true; break;
                case "isunplayed": query.IsPlayed = false; break;
                case "isresumable": query.IsResumable = true; break;
            }
        }
        query.Sort = ParseSort(q);
        if (presorted && !query.IsRandom) query.Sort = [.. BoxSetOrder];
        return query;
    }

    private static List<(string, bool)> ParseSort(IQueryCollection q)
    {
        var by = q.List("sortBy");
        var order = q.List("sortOrder");
        return by.Select((field, i) =>
        {
            var o = i < order.Length ? order[i] : order.LastOrDefault();
            return (field, string.Equals(o, "Descending", StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private static async Task<(List<BaseItemDto> Items, int Total)> RunAsync(HttpContext ctx, ItemQuery query, ItemRepository items, DtoBuilder dto, FieldSet? fields = null)
    {
        var page = await items.QueryAsync(query, ctx.RequestAborted);
        var list = await items.GetManyAsync(page.Ids, ctx.RequestAborted);
        return (await dto.BuildAsync(list, ctx.Auth(), fields ?? FieldSet.Parse(ctx.Request), ctx.RequestAborted), page.Total);
    }

    private static async Task<IResult> GetItems(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ServerIdentity identity,
        ICollectionManager collections, IPeopleStore people, CollectionListing listing)
    {
        var q = ctx.Request.Query;
        var parentId = q.Guid("parentId");
        var me = ctx.Auth().User.Id;

        // The playlists and collections views list the lists; a playlist lists its lines in its own order (shuffled when asked).
        if (parentId == CollectionViews.PlaylistsId) return await CollectionPage(ctx, [CollectionKinds.Playlist], null, listing, dto);
        if (parentId == CollectionViews.BoxSetsId) return await CollectionPage(ctx, [CollectionKinds.BoxSet], null, listing, dto);
        if (parentId is { } pid && libs.Find(pid) is null && await collections.GetAsync(pid, ctx.RequestAborted) is { IsPlaylist: true } playlist)
        {
            if (!playlist.IsVisibleTo(me)) return SystemEndpoints.EmptyResult();
            var random = q.List("sortBy").Contains("Random", StringComparer.OrdinalIgnoreCase);
            var (lines, count, start) = await CollectionEndpoints.PlaylistPageAsync(ctx, playlist, collections, items, dto, random);
            return Page(lines, count, start);
        }

        // Playlists and box sets asked for by type: on their own, or next to catalog items (jellyfin-web's search asks for all at once).
        var kinds = q.List("includeItemTypes").Where(CollectionKinds.IsKind).Select(CollectionKinds.Normalize).Distinct().ToArray();
        var library = parentId is { } p && libs.Find(p) is { } lib ? lib.Id : (Guid?)null;
        if (kinds.Length > 0 && !q.List("includeItemTypes").Any(SupportedTypes.Contains))
            return await CollectionPage(ctx, kinds, library, listing, dto);

        var query = await BuildQueryAsync(ctx, libs, items, identity, collections, people);
        List<BaseItemDto> list = [];
        var total = 0;
        if (query is not null) (list, total) = await RunAsync(ctx, query, items, dto);
        if (kinds.Length > 0 && (q.Int("startIndex") ?? 0) == 0)
        {
            var (extra, extraTotal) = await listing.QueryAsync(ctx, kinds, library);
            list.AddRange(await dto.CollectionsAsync(extra, ctx.Auth(), ctx.RequestAborted));
            total += extraTotal;
        }
        return Page(list, total, query?.StartIndex ?? 0);
    }

    private static async Task<IResult> CollectionPage(HttpContext ctx, IReadOnlyCollection<string> kinds, Guid? library, CollectionListing listing, DtoBuilder dto)
    {
        var (page, total) = await listing.QueryAsync(ctx, kinds, library);
        return Page(await dto.CollectionsAsync(page, ctx.Auth(), ctx.RequestAborted), total, Math.Max(0, ctx.Request.Query.Int("startIndex") ?? 0));
    }

    private static async Task<IResult> GetItem(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ServerIdentity identity,
        ICollectionManager collections, IPeopleStore people)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        if (libs.Find(id) is { } lib)
        {
            if (!await ctx.CanAccessLibraryAsync(lib.Id)) return Results.NotFound();
            var counts = await items.LibraryChildCountsAsync(ctx.Auth().User.Id, ctx.RequestAborted);
            return Results.Json(dto.LibraryDto(lib, counts.GetValueOrDefault(lib.Id)), AppJson.Default.BaseItemDto);
        }
        if (id == identity.RootFolderId)
        {
            var restricted = await ctx.RestrictionsAsync();
            return Results.Json(new BaseItemDto
            {
                Name = "Media Folders", ServerId = identity.ServerId, Id = id.N(), Type = "UserRootFolder", IsFolder = true,
                ChildCount = libs.All.Count(l => restricted.AllowsLibrary(l.Id)),
            }, AppJson.Default.BaseItemDto);
        }
        if (id == CollectionViews.PlaylistsId) return Results.Json(dto.PlaylistsViewDto(), AppJson.Default.BaseItemDto);
        if (id == CollectionViews.BoxSetsId) return Results.Json(dto.BoxSetsViewDto(), AppJson.Default.BaseItemDto);

        var item = await items.GetAsync(id, ctx.RequestAborted);
        if (item is not null && !await ctx.CanAccessAsync(item)) return Results.NotFound();
        if (item is not null) return Results.Json(await dto.BuildAsync(item, ctx.Auth(), FieldSet.All, ctx.RequestAborted), AppJson.Default.BaseItemDto);
        if (await collections.GetAsync(id, ctx.RequestAborted) is { } collection)
        {
            var me = ctx.Auth().User.Id;
            if (!collection.IsVisibleTo(me) || (await ctx.RequestServices.GetRequiredService<CollectionListing>().VisibleAsync(me, [collection], ctx.RequestAborted)).Count == 0)
                return Results.NotFound();
            return Results.Json((await dto.CollectionsAsync([collection], ctx.Auth(), ctx.RequestAborted))[0], AppJson.Default.BaseItemDto);
        }
        if ((await people.GetPersonsAsync([id], ctx.RequestAborted)).FirstOrDefault() is { } person)
            return Results.Json((await dto.PersonsAsync([person], ctx.Auth(), ctx.RequestAborted))[0], AppJson.Default.BaseItemDto);
        return Results.NotFound();
    }

    private static async Task<IResult> Latest(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto)
    {
        var q = ctx.Request.Query;
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, Limit = Math.Clamp(q.Int("limit") ?? 16, 1, 100), IncludeTotal = false,
            Sort = [("DateLastContentAdded", true)],
        };
        var requested = q.List("includeItemTypes").Where(SupportedTypes.Contains).ToArray();
        if (q.Guid("parentId") is { } parentId && libs.Find(parentId) is { } lib)
        {
            query.LibraryIds = [lib.Id];
            query.Types = requested.Length > 0 ? requested : LibraryTypes.TopLevelTypes(lib.CollectionType);
        }
        else
        {
            query.Types = requested.Length > 0 ? requested : [ItemTypes.Movie, ItemTypes.Series];
        }
        var (list, _) = await RunAsync(ctx, query, items, dto);
        return Results.Json(list, AppJson.Default.ListBaseItemDto);
    }

    private static async Task<IResult> Resume(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto)
    {
        var q = ctx.Request.Query;
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, StartIndex = q.Int("startIndex") ?? 0, Limit = q.Int("limit") ?? 12,
            Types = [ItemTypes.Movie, ItemTypes.Episode], IsResumable = true, Sort = [("DatePlayed", true)],
        };
        if (q.Guid("parentId") is { } parentId && libs.Find(parentId) is { } lib) query.LibraryIds = [lib.Id];
        var (list, total) = await RunAsync(ctx, query, items, dto);
        return Page(list, total, query.StartIndex);
    }

    private static async Task<IResult> NextUp(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto)
    {
        var q = ctx.Request.Query;
        var start = q.Int("startIndex") ?? 0;
        Guid[]? libraryIds = q.Guid("parentId") is { } p && libs.Find(p) is { } lib ? [lib.Id] : null;
        var ids = await items.NextUpAsync(ctx.Auth().User.Id, q.Guid("seriesId"), libraryIds, start, Math.Clamp(q.Int("limit") ?? 24, 1, 200), ctx.RequestAborted);
        var list = await dto.BuildAsync(await items.GetManyAsync(ids, ctx.RequestAborted), ctx.Auth(), FieldSet.Parse(ctx.Request), ctx.RequestAborted);
        return Page(list, start + list.Count, start);
    }

    private static async Task<IResult> Seasons(HttpContext ctx, ItemRepository items, DtoBuilder dto)
    {
        if (ctx.RouteGuid("id") is not { } seriesId) return Results.NotFound();
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, ParentId = seriesId, Types = [ItemTypes.Season], Sort = [("IndexNumber", false)],
        };
        var (list, total) = await RunAsync(ctx, query, items, dto);
        return Page(list, total, 0);
    }

    private static async Task<IResult> Episodes(HttpContext ctx, ItemRepository items, DtoBuilder dto)
    {
        if (ctx.RouteGuid("id") is not { } seriesId) return Results.NotFound();
        var q = ctx.Request.Query;
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, SeriesId = seriesId, Types = [ItemTypes.Episode],
            SeasonId = q.Guid("seasonId"), ParentIndexNumber = q.Int("season"), Sort = [("AiredEpisodeOrder", false)],
        };
        var page = await items.QueryAsync(query, ctx.RequestAborted);
        IEnumerable<Guid> ids = page.Ids;
        // StartItemId (used by "play from here") is resolved in memory: a series is small.
        if (q.Guid("startItemId") is { } startItem && page.Ids.IndexOf(startItem) is var idx and >= 0) ids = ids.Skip(idx);
        var start = q.Int("startIndex") ?? 0;
        ids = ids.Skip(start);
        if (q.Int("limit") is { } limit) ids = ids.Take(limit);
        var list = await dto.BuildAsync(await items.GetManyAsync(ids.ToList(), ctx.RequestAborted), ctx.Auth(), FieldSet.Parse(ctx.Request), ctx.RequestAborted);
        return Page(list, page.Total, start);
    }

    private static async Task<IResult> Similar(HttpContext ctx, ItemRepository items, DtoBuilder dto)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item || item.Genres.Length == 0
            || !await ctx.CanAccessAsync(item))
            return SystemEndpoints.EmptyResult();
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, Types = [item.Type is ItemTypes.Series ? ItemTypes.Series : ItemTypes.Movie],
            Genres = item.Genres, ExcludeIds = [id], Limit = Math.Clamp(ctx.Request.Query.Int("limit") ?? 12, 1, 50),
            Sort = [("CommunityRating", true)], IncludeTotal = false,
        };
        var (list, total) = await RunAsync(ctx, query, items, dto);
        return Page(list, total, 0);
    }

    private static async Task<IResult> Ancestors(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ICollectionManager collections)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        if (await items.GetAsync(id) is not { } item)
        {
            // A playlist or box set sits in its view.
            if (await collections.GetAsync(id, ctx.RequestAborted) is not { } c) return Results.NotFound();
            return Results.Json(new List<BaseItemDto> { c.IsPlaylist ? dto.PlaylistsViewDto() : dto.BoxSetsViewDto() }, AppJson.Default.ListBaseItemDto);
        }
        if (!await ctx.CanAccessAsync(item)) return Results.NotFound();
        var parentIds = new[] { item.SeasonId, item.SeriesId }.OfType<Guid>().ToList();
        var result = await dto.BuildAsync(await items.GetManyAsync(parentIds), ctx.Auth(), FieldSet.Parse(ctx.Request));
        if (libs.Find(item.LibraryId) is { } lib) result.Add(dto.LibraryDto(lib, 0));
        return Results.Json(result, AppJson.Default.ListBaseItemDto);
    }

    // ---- search / filters ----

    private static async Task<IResult> SearchHints(HttpContext ctx, LibraryRegistry libs, ItemRepository items, ServerIdentity identity,
        ICollectionManager collections, IPeopleStore people, CollectionListing listing)
    {
        var q = ctx.Request.Query;
        var term = q.Str("searchTerm");
        if (term is null) return Results.Json(new SearchHintResult(), AppJson.Default.SearchHintResult);
        var requested = q.List("includeItemTypes");
        var types = requested.Where(SupportedTypes.Contains).ToArray();
        bool Wants(string type) => requested.Length == 0 || requested.Contains(type, StringComparer.OrdinalIgnoreCase);
        var limit = Math.Clamp(q.Int("limit") ?? 20, 1, 100);
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, SearchTerm = ItemNaming.SearchText(term), StartIndex = q.Int("startIndex") ?? 0,
            Limit = limit, Types = types.Length > 0 ? types : [ItemTypes.Movie, ItemTypes.Series],
        };
        if (q.Guid("parentId") is { } p && libs.Find(p) is { } lib) query.LibraryIds = [lib.Id];
        var page = requested.Length > 0 && types.Length == 0 ? new QueryPage([], 0) : await items.QueryAsync(query, ctx.RequestAborted);
        var list = await items.GetManyAsync(page.Ids, ctx.RequestAborted);
        var extra = new List<SearchHint>();
        if (query.StartIndex == 0 && query.LibraryIds is null)
        {
            // Lists and people come after the catalog's matches, like Jellyfin's mixed results.
            foreach (var kind in new[] { CollectionKinds.BoxSet, CollectionKinds.Playlist }.Where(Wants))
                foreach (var c in (await listing.VisibleAsync(ctx.Auth().User.Id, await collections.ListAsync(kind, ctx.Auth().User.Id, ctx.RequestAborted), ctx.RequestAborted))
                         .Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(limit))
                    extra.Add(new SearchHint { ItemId = c.Id.N(), Id = c.Id.N(), Name = c.Name, Type = c.Kind, IsFolder = true, MediaType = c.MediaType ?? "Unknown" });
            var visible = await items.VisibleIdsAsync(ctx.Auth().User.Id, ctx.RequestAborted);
            if (Wants("Person") && visible is not { Length: 0 })
                foreach (var person in (await people.QueryAsync(new PersonQuery { SearchTerm = term, Limit = limit, AppearsInItemIds = visible }, ctx.RequestAborted)).People)
                    extra.Add(new SearchHint
                    {
                        ItemId = person.Id.N(), Id = person.Id.N(), Name = person.Name, Type = "Person",
                        PrimaryImageTag = person.ImageUrl is { } img ? DtoBuilder.ImageTag(img, "") : null, PrimaryImageAspectRatio = 2.0 / 3.0,
                    });
        }
        var hints = list.Select(it => new SearchHint
        {
            ItemId = it.Id.N(), Id = it.Id.N(), Name = it.Name, ProductionYear = it.ProductionYear, Type = it.Type,
            IsFolder = it.IsFolder, RunTimeTicks = it.RuntimeTicks > 0 ? it.RuntimeTicks : null,
            MediaType = it.IsFolder ? "Unknown" : "Video", IndexNumber = it.IndexNumber, ParentIndexNumber = it.ParentIndexNumber,
            PrimaryImageTag = it.Images.Primary is { } pi ? DtoBuilder.ImageTag(pi, it.Fingerprint) : null,
            BackdropImageTag = it.Images.Backdrops.FirstOrDefault() is { } b ? DtoBuilder.ImageTag(b, it.Fingerprint) : null,
            BackdropImageItemId = it.Images.Backdrops.Count > 0 ? it.Id.N() : null,
            ThumbImageTag = it.Images.Thumb is { } t ? DtoBuilder.ImageTag(t, it.Fingerprint) : null,
            ThumbImageItemId = it.Images.Thumb is not null ? it.Id.N() : null,
            PrimaryImageAspectRatio = it.Type == ItemTypes.Episode ? 16.0 / 9.0 : 2.0 / 3.0,
        }).Concat(extra).ToList();
        return Results.Json(new SearchHintResult { SearchHints = hints, TotalRecordCount = page.Total + extra.Count }, AppJson.Default.SearchHintResult);
    }

    private static Guid[]? LibraryFilter(HttpContext ctx, LibraryRegistry libs) =>
        ctx.Request.Query.Guid("parentId") is { } p && libs.Find(p) is { } lib ? [lib.Id] : null;

    private static async Task<IResult> Genres(HttpContext ctx, LibraryRegistry libs, ItemRepository items, ServerIdentity identity)
    {
        var q = ctx.Request.Query;
        IEnumerable<(Guid Id, string Name, int Count)> genres = await items.GenresAsync(ctx.Auth().User.Id, LibraryFilter(ctx, libs), ctx.RequestAborted);
        if (q.Str("searchTerm") is { } term) genres = genres.Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (q.Str("nameStartsWith") is { } prefix) genres = genres.Where(g => g.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var all = genres.ToList();
        var start = q.Int("startIndex") ?? 0;
        var dtos = all.Skip(start).Take(q.Int("limit") ?? int.MaxValue).Select(g => new BaseItemDto
        {
            Name = g.Name, ServerId = identity.ServerId, Id = g.Id.N(), Type = "Genre", IsFolder = true,
            ChildCount = g.Count, SortName = g.Name.ToLowerInvariant(),
        }).ToList();
        return Page(dtos, all.Count, start);
    }

    private static async Task<IResult> Genre(HttpContext ctx, ServerIdentity identity, ItemRepository items)
    {
        var name = Uri.UnescapeDataString(ctx.Request.RouteValues["name"]?.ToString() ?? "");
        var match = (await items.GenresAsync(ctx.Auth().User.Id, null, ctx.RequestAborted)).FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match.Name is null) return Results.NotFound();
        return Results.Json(new BaseItemDto { Name = match.Name, ServerId = identity.ServerId, Id = match.Id.N(), Type = "Genre", IsFolder = true, ChildCount = match.Count },
            AppJson.Default.BaseItemDto);
    }

    private static async Task<IResult> Filters(HttpContext ctx, LibraryRegistry libs, ItemRepository items)
    {
        var libFilter = LibraryFilter(ctx, libs);
        var me = ctx.Auth().User.Id;
        var genres = await items.GenresAsync(me, libFilter, ctx.RequestAborted);
        return Results.Json(new QueryFiltersLegacy
        {
            Genres = genres.Select(g => g.Name).ToList(), Years = await items.YearsAsync(me, libFilter, ctx.RequestAborted),
        }, AppJson.Default.QueryFiltersLegacy);
    }

    private static async Task<IResult> Filters2(HttpContext ctx, LibraryRegistry libs, ItemRepository items)
    {
        var genres = await items.GenresAsync(ctx.Auth().User.Id, LibraryFilter(ctx, libs), ctx.RequestAborted);
        return Results.Json(new QueryFilters { Genres = genres.Select(g => new NameIdPair(g.Name, g.Id.N())).ToList() }, AppJson.Default.QueryFilters);
    }

    private static async Task<IResult> Counts(HttpContext ctx, ItemRepository items, ICollectionManager collections, CollectionListing listing)
    {
        var c = await items.CountsAsync(ctx.Auth().User.Id, ctx.RequestAborted);
        var me = ctx.Auth().User.Id;
        var boxSets = (await listing.VisibleAsync(me, await collections.ListAsync(CollectionKinds.BoxSet, me, ctx.RequestAborted), ctx.RequestAborted)).Count;
        return Results.Json(new ItemCounts
        {
            MovieCount = c.GetValueOrDefault(ItemTypes.Movie), SeriesCount = c.GetValueOrDefault(ItemTypes.Series),
            EpisodeCount = c.GetValueOrDefault(ItemTypes.Episode), BoxSetCount = boxSets, ItemCount = c.Values.Sum() + boxSets,
        }, AppJson.Default.ItemCounts);
    }
}
