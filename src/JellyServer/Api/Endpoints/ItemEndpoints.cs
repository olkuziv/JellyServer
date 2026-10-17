using JellyServer.Scanning;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Library browsing: views, item queries, latest, resume, next up, shows, search, genres.</summary>
public static class ItemEndpoints
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
        { ItemTypes.Movie, ItemTypes.Series, ItemTypes.Season, ItemTypes.Episode };

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

    private static async Task<IResult> Views(LibraryRegistry libs, ItemRepository items, DtoBuilder dto)
    {
        var counts = await items.LibraryChildCountsAsync();
        var views = libs.All.Select(l => dto.LibraryDto(l, counts.GetValueOrDefault(l.Id))).ToList();
        return Page(views, views.Count, 0);
    }

    private static async Task<IResult> MediaFolders(LibraryRegistry libs, ItemRepository items, DtoBuilder dto) =>
        await Views(libs, items, dto);

    // ---- generic item query ----

    /// <summary>Maps Jellyfin query parameters onto <see cref="ItemQuery"/>. Returns null when nothing can match.</summary>
    private static async Task<ItemQuery?> BuildQueryAsync(HttpContext ctx, LibraryRegistry libs, ItemRepository items, ServerIdentity identity)
    {
        var q = ctx.Request.Query;
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id,
            StartIndex = q.Int("startIndex") ?? 0,
            Limit = q.Int("limit"),
            IncludeTotal = q.Bool("enableTotalRecordCount") ?? true,
        };
        var recursive = q.Bool("recursive") ?? false;

        var requestedTypes = q.List("includeItemTypes");
        var types = requestedTypes.Where(SupportedTypes.Contains)
            .Select(t => SupportedTypes.First(s => s.Equals(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (requestedTypes.Length > 0 && types.Length == 0) return null; // e.g. BoxSet, MusicAlbum
        var mediaTypes = q.List("mediaTypes");
        if (types.Length == 0 && mediaTypes.Length > 0)
        {
            if (!mediaTypes.Contains("Video", StringComparer.OrdinalIgnoreCase)) return null;
            types = [ItemTypes.Movie, ItemTypes.Episode];
        }
        var exclude = q.List("excludeItemTypes").Where(SupportedTypes.Contains).ToArray();

        if (q.Guid("parentId") is { } parentId && parentId != identity.RootFolderId)
        {
            if (libs.Find(parentId) is { } lib)
            {
                query.LibraryIds = [lib.Id];
                if (!recursive && types.Length == 0)
                    types = LibraryTypes.TopLevelTypes(lib.CollectionType);
            }
            else if (await items.GetAsync(parentId) is { } parent)
            {
                switch (parent.Type)
                {
                    case ItemTypes.Series when recursive: query.SeriesId = parent.Id; break;
                    case ItemTypes.Series: query.ParentId = parent.Id; break;
                    case ItemTypes.Season: query.SeasonId = parent.Id; break;
                    default: return null; // leaf items have no children
                }
            }
            else
            {
                return null;
            }
        }
        else if (!recursive && types.Length == 0 && q.List("ids").Length == 0)
        {
            types = [ItemTypes.Movie, ItemTypes.Series];
        }

        query.Types = types.Length > 0 ? types : null;
        query.ExcludeTypes = exclude.Length > 0 ? exclude : null;
        if (q.Guid("seriesId") is { } seriesId) query.SeriesId = seriesId;
        if (q.Guid("seasonId") is { } seasonId) query.SeasonId = seasonId;
        if (q.Guids("ids") is { Length: > 0 } ids) query.Ids = ids;
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

    private static async Task<IResult> GetItems(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ServerIdentity identity)
    {
        var query = await BuildQueryAsync(ctx, libs, items, identity);
        if (query is null) return SystemEndpoints.EmptyResult();
        var (list, total) = await RunAsync(ctx, query, items, dto);
        return Page(list, total, query.StartIndex);
    }

    private static async Task<IResult> GetItem(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto, ServerIdentity identity)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        if (libs.Find(id) is { } lib)
        {
            var counts = await items.LibraryChildCountsAsync();
            return Results.Json(dto.LibraryDto(lib, counts.GetValueOrDefault(lib.Id)), AppJson.Default.BaseItemDto);
        }
        if (id == identity.RootFolderId)
        {
            return Results.Json(new BaseItemDto
            {
                Name = "Media Folders", ServerId = identity.ServerId, Id = id.N(), Type = "UserRootFolder", IsFolder = true,
                ChildCount = libs.All.Count,
            }, AppJson.Default.BaseItemDto);
        }
        var item = await items.GetAsync(id, ctx.RequestAborted);
        if (item is null) return Results.NotFound();
        return Results.Json(await dto.BuildAsync(item, ctx.Auth(), FieldSet.All, ctx.RequestAborted), AppJson.Default.BaseItemDto);
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
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item || item.Genres.Length == 0)
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

    private static async Task<IResult> Ancestors(HttpContext ctx, LibraryRegistry libs, ItemRepository items, DtoBuilder dto)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item) return Results.NotFound();
        var parentIds = new[] { item.SeasonId, item.SeriesId }.OfType<Guid>().ToList();
        var result = await dto.BuildAsync(await items.GetManyAsync(parentIds), ctx.Auth(), FieldSet.Parse(ctx.Request));
        if (libs.Find(item.LibraryId) is { } lib) result.Add(dto.LibraryDto(lib, 0));
        return Results.Json(result, AppJson.Default.ListBaseItemDto);
    }

    // ---- search / filters ----

    private static async Task<IResult> SearchHints(HttpContext ctx, LibraryRegistry libs, ItemRepository items, ServerIdentity identity)
    {
        var q = ctx.Request.Query;
        var term = q.Str("searchTerm");
        if (term is null) return Results.Json(new SearchHintResult(), AppJson.Default.SearchHintResult);
        var types = q.List("includeItemTypes").Where(SupportedTypes.Contains).ToArray();
        var query = new ItemQuery
        {
            UserId = ctx.Auth().User.Id, SearchTerm = ItemNaming.SearchText(term), StartIndex = q.Int("startIndex") ?? 0,
            Limit = Math.Clamp(q.Int("limit") ?? 20, 1, 100),
            Types = types.Length > 0 ? types : [ItemTypes.Movie, ItemTypes.Series, ItemTypes.Episode],
        };
        if (q.Guid("parentId") is { } p && libs.Find(p) is { } lib) query.LibraryIds = [lib.Id];
        var page = await items.QueryAsync(query, ctx.RequestAborted);
        var list = await items.GetManyAsync(page.Ids, ctx.RequestAborted);
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
        }).ToList();
        return Results.Json(new SearchHintResult { SearchHints = hints, TotalRecordCount = page.Total }, AppJson.Default.SearchHintResult);
    }

    private static Guid[]? LibraryFilter(HttpContext ctx, LibraryRegistry libs) =>
        ctx.Request.Query.Guid("parentId") is { } p && libs.Find(p) is { } lib ? [lib.Id] : null;

    private static async Task<IResult> Genres(HttpContext ctx, LibraryRegistry libs, ItemRepository items, ServerIdentity identity)
    {
        var q = ctx.Request.Query;
        IEnumerable<(Guid Id, string Name, int Count)> genres = await items.GenresAsync(LibraryFilter(ctx, libs), ctx.RequestAborted);
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
        var match = (await items.GenresAsync(null)).FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match.Name is null) return Results.NotFound();
        return Results.Json(new BaseItemDto { Name = match.Name, ServerId = identity.ServerId, Id = match.Id.N(), Type = "Genre", IsFolder = true, ChildCount = match.Count },
            AppJson.Default.BaseItemDto);
    }

    private static async Task<IResult> Filters(HttpContext ctx, LibraryRegistry libs, ItemRepository items)
    {
        var libFilter = LibraryFilter(ctx, libs);
        var genres = await items.GenresAsync(libFilter, ctx.RequestAborted);
        return Results.Json(new QueryFiltersLegacy
        {
            Genres = genres.Select(g => g.Name).ToList(), Years = await items.YearsAsync(libFilter, ctx.RequestAborted),
        }, AppJson.Default.QueryFiltersLegacy);
    }

    private static async Task<IResult> Filters2(HttpContext ctx, LibraryRegistry libs, ItemRepository items)
    {
        var genres = await items.GenresAsync(LibraryFilter(ctx, libs), ctx.RequestAborted);
        return Results.Json(new QueryFilters { Genres = genres.Select(g => new NameIdPair(g.Name, g.Id.N())).ToList() }, AppJson.Default.QueryFilters);
    }

    private static async Task<IResult> Counts(ItemRepository items)
    {
        var c = await items.CountsAsync();
        return Results.Json(new ItemCounts
        {
            MovieCount = c.GetValueOrDefault(ItemTypes.Movie), SeriesCount = c.GetValueOrDefault(ItemTypes.Series),
            EpisodeCount = c.GetValueOrDefault(ItemTypes.Episode), ItemCount = c.Values.Sum(),
        }, AppJson.Default.ItemCounts);
    }
}
