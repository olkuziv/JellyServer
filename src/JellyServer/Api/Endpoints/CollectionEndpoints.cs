using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Kernel;
using JellyServer.Serialization;

namespace JellyServer.Api.Endpoints;

/// <summary>
/// Jellyfin's PlaylistsController and CollectionController, plus deleting playlists and collections through <c>DELETE /Items</c>.
/// The lists themselves are kept by the Collections module; their items are catalog items.
/// </summary>
public static class CollectionEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("").RequireToken();
        g.MapPost("/Playlists", CreatePlaylist);
        g.MapPost("/Playlists/{playlistId}", UpdatePlaylist);
        g.MapGet("/Playlists/{playlistId}", GetPlaylist);
        g.MapGet("/Playlists/{playlistId}/Items", GetPlaylistItems);
        g.MapPost("/Playlists/{playlistId}/Items", AddToPlaylist);
        g.MapDelete("/Playlists/{playlistId}/Items", RemoveFromPlaylist);
        g.MapPost("/Playlists/{playlistId}/Items/{itemId}/Move/{newIndex:int}", MoveItem);
        g.MapGet("/Playlists/{playlistId}/Users", GetPlaylistUsers);
        g.MapGet("/Playlists/{playlistId}/Users/{userId}", GetPlaylistUser);
        g.MapPost("/Playlists/{playlistId}/Users/{userId}", UpdatePlaylistUser);
        g.MapDelete("/Playlists/{playlistId}/Users/{userId}", RemovePlaylistUser);

        // Jellyfin's CollectionManagement policy: administrators and users granted EnableCollectionManagement.
        var manage = app.MapGroup("/Collections").RequireToken().AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Policy().EnableCollectionManagement ? await next(ctx) : Forbidden());
        manage.MapPost("", CreateCollection);
        manage.MapPost("/{collectionId}/Items", AddToCollection);
        manage.MapDelete("/{collectionId}/Items", RemoveFromCollection);

        g.MapDelete("/Items/{id}", DeleteItem);
        g.MapDelete("/Items", DeleteItems);
    }

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private static Guid[] ParseIds(IEnumerable<string>? ids) =>
        (ids ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Ids.Parse).OfType<Guid>().ToArray();

    private static List<CollectionShare>? Shares(List<PlaylistUserPermissionsDto>? users) =>
        users?.Select(u => Ids.Parse(u.UserId) is { } id ? new CollectionShare(id, u.CanEdit) : null).OfType<CollectionShare>().ToList();

    private static PlaylistUserPermissionsDto Dto(CollectionShare s) => new() { UserId = s.UserId.N(), CanEdit = s.CanEdit };

    /// <summary>The playlist as the caller may see it (Jellyfin's GetPlaylistForUser): null when missing or hidden.</summary>
    private static async Task<Collection?> PlaylistFor(HttpContext ctx, ICollectionManager collections, Guid userId)
    {
        if (ctx.RouteGuid("playlistId") is not { } id) return null;
        return await collections.GetAsync(id, ctx.RequestAborted) is { IsPlaylist: true } p && p.IsVisibleTo(userId) ? p : null;
    }

    /// <summary>The user a request acts for: the given one when it is the caller or the caller is an administrator.</summary>
    private static Guid? ActingUser(HttpContext ctx, Guid? requested)
    {
        var me = ctx.Auth().User;
        if (requested is not { } id || id == Guid.Empty || id == me.Id) return me.Id;
        return me.IsAdmin ? id : null;
    }

    // ---- playlists ----

    /// <summary>Only the items the caller may see can be put into a list.</summary>
    private static async Task<Guid[]> Visible(HttpContext ctx, Guid[] ids)
    {
        if (ids.Length == 0) return ids;
        var visible = await ctx.RequestServices.GetRequiredService<ItemRepository>().VisibleAsync(ctx.Auth().User.Id, ids, ctx.RequestAborted);
        return [.. ids.Where(visible.Contains)];
    }

    private static async Task<IResult> CreatePlaylist(HttpContext ctx, ICollectionManager collections)
    {
        var q = ctx.Request.Query;
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.CreatePlaylistDto);
        // Query parameters (obsolete in Jellyfin) win over the body.
        var name = q.Str("name") ?? body?.Name;
        if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest("Name is required");
        var ids = await Visible(ctx, q.Guids("ids") is { Length: > 0 } fromQuery ? fromQuery : ParseIds(body?.Ids));
        if (ActingUser(ctx, q.Guid("userId") ?? Ids.Parse(body?.UserId)) is not { } owner) return Forbidden();
        var playlist = await collections.CreatePlaylistAsync(owner, name.Trim(), ids, q.Str("mediaType") ?? body?.MediaType,
            Shares(body?.Users) ?? [], body?.IsPublic ?? false, ctx.RequestAborted);
        return Results.Json(new PlaylistCreationResult { Id = playlist.Id.N() }, AppJson.Default.PlaylistCreationResult);
    }

    private static async Task<IResult> UpdatePlaylist(HttpContext ctx, ICollectionManager collections)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (!playlist.CanEdit(me)) return Forbidden();
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.UpdatePlaylistDto);
        if (body is null) return Results.BadRequest();
        await collections.UpdatePlaylistAsync(playlist.Id, body.Name?.Trim() is { Length: > 0 } n ? n : null,
            body.Ids is null ? null : await Visible(ctx, ParseIds(body.Ids)), Shares(body.Users), body.IsPublic, ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPlaylist(HttpContext ctx, ICollectionManager collections, ItemRepository items)
    {
        if (await PlaylistFor(ctx, collections, ctx.Auth().User.Id) is not { } playlist) return Results.NotFound("Playlist not found");
        var all = await collections.EntriesAsync(playlist.Id, ctx.RequestAborted);
        var visible = await items.VisibleAsync(ctx.Auth().User.Id, all, ctx.RequestAborted);
        var entries = all.Where(visible.Contains).ToList();
        return Results.Json(new PlaylistDto
        {
            OpenAccess = playlist.OpenAccess, Shares = playlist.Shares.Select(Dto).ToList(), ItemIds = entries.Select(e => e.N()).ToList(),
        }, AppJson.Default.PlaylistDto);
    }

    private static async Task<IResult> GetPlaylistItems(HttpContext ctx, ICollectionManager collections, ItemRepository items, DtoBuilder dto)
    {
        // Jellyfin takes the userId parameter as is; here another user's view is an administrator's privilege.
        var userId = ActingUser(ctx, ctx.Request.Query.Guid("userId")) ?? ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, userId) is not { } playlist) return Results.NotFound("Playlist not found");
        var (page, total, start) = await PlaylistPageAsync(ctx, playlist, collections, items, dto, random: false);
        return Results.Json(new QueryResult<BaseItemDto> { Items = page, TotalRecordCount = total, StartIndex = start }, AppJson.Default.QueryResultBaseItemDto);
    }

    /// <summary>
    /// A page of a playlist in its order (or shuffled), each line marked with its PlaylistItemId. Lines whose item is gone are left out,
    /// as Jellyfin skips what the user cannot see.
    /// </summary>
    internal static async Task<(List<BaseItemDto> Items, int Total, int Start)> PlaylistPageAsync(HttpContext ctx, Collection playlist,
        ICollectionManager collections, ItemRepository items, DtoBuilder dto, bool random)
    {
        var q = ctx.Request.Query;
        var entries = await collections.EntriesAsync(playlist.Id, ctx.RequestAborted);
        // What the viewer may not see (another library, a rating above their limit) is left out of the lines.
        var loaded = await items.GetManyAsync(entries, ctx.RequestAborted);
        var visible = await items.VisibleAsync(ctx.Auth().User.Id, loaded.Select(l => l.PrimaryId ?? l.Id).ToList(), ctx.RequestAborted);
        IEnumerable<Item> lines = loaded.Where(l => visible.Contains(l.PrimaryId ?? l.Id));
        if (random) lines = lines.OrderBy(_ => Random.Shared.Next());
        var all = lines.ToList();
        var start = Math.Max(0, q.Int("startIndex") ?? 0);
        IEnumerable<Item> page = all.Skip(start);
        if (q.Int("limit") is > 0 and var limit) page = page.Take(limit);
        var list = page.ToList();
        var dtos = await dto.BuildAsync(list, ctx.Auth(), FieldSet.Parse(ctx.Request), ctx.RequestAborted);
        for (var i = 0; i < dtos.Count; i++) dtos[i].PlaylistItemId = list[i].Id.N();
        return (dtos, all.Count, start);
    }

    private static async Task<IResult> AddToPlaylist(HttpContext ctx, ICollectionManager collections)
    {
        var q = ctx.Request.Query;
        if (ActingUser(ctx, q.Guid("userId")) is not { } userId) return Forbidden();
        if (await PlaylistFor(ctx, collections, userId) is not { } playlist) return Results.NotFound("Playlist not found");
        if (!playlist.CanEdit(userId)) return Forbidden();
        await collections.AddAsync(playlist.Id, await Visible(ctx, q.Guids("ids")), q.Int("position"), ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFromPlaylist(HttpContext ctx, ICollectionManager collections)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (!playlist.CanEdit(me)) return Forbidden();
        await collections.RemoveAsync(playlist.Id, ctx.Request.Query.Guids("entryIds"), ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> MoveItem(HttpContext ctx, ICollectionManager collections, int newIndex)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (!playlist.CanEdit(me)) return Forbidden();
        if (ctx.RouteGuid("itemId") is not { } itemId) return Results.NotFound();
        await collections.MoveAsync(playlist.Id, itemId, newIndex, ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPlaylistUsers(HttpContext ctx, ICollectionManager collections)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (playlist.OwnerId != me) return Forbidden();
        return Results.Json(playlist.Shares.Select(Dto).ToList(), AppJson.Default.ListPlaylistUserPermissionsDto);
    }

    /// <summary>Jellyfin answers the owner with "may edit" whatever user is asked about; jellyfin-web relies on it to list editable playlists.</summary>
    private static async Task<IResult> GetPlaylistUser(HttpContext ctx, ICollectionManager collections)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (playlist.OwnerId == me) return Results.Json(new PlaylistUserPermissionsDto { UserId = me.N(), CanEdit = true }, AppJson.Default.PlaylistUserPermissionsDto);
        if (ctx.RouteGuid("userId") is not { } userId) return Results.NotFound();
        if (!playlist.CanEdit(me) && userId != me) return Forbidden();
        return playlist.Shares.FirstOrDefault(s => s.UserId == userId) is { } share
            ? Results.Json(Dto(share), AppJson.Default.PlaylistUserPermissionsDto)
            : Results.NotFound("User permissions not found");
    }

    private static async Task<IResult> UpdatePlaylistUser(HttpContext ctx, ICollectionManager collections)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (playlist.OwnerId != me) return Forbidden();
        if (ctx.RouteGuid("userId") is not { } userId) return Results.NotFound();
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.UpdatePlaylistUserDto);
        await collections.SetShareAsync(playlist.Id, userId, body?.CanEdit ?? false, ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemovePlaylistUser(HttpContext ctx, ICollectionManager collections)
    {
        var me = ctx.Auth().User.Id;
        if (await PlaylistFor(ctx, collections, me) is not { } playlist) return Results.NotFound("Playlist not found");
        if (!playlist.CanEdit(me)) return Forbidden();
        if (ctx.RouteGuid("userId") is not { } userId || playlist.Shares.All(s => s.UserId != userId)) return Results.NotFound("User permissions not found");
        await collections.RemoveShareAsync(playlist.Id, userId, ctx.RequestAborted);
        return Results.NoContent();
    }

    // ---- collections (box sets) ----

    private static async Task<IResult> CreateCollection(HttpContext ctx, ICollectionManager collections)
    {
        var q = ctx.Request.Query;
        var name = q.Str("name");
        if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest("Name is required");
        var boxSet = await collections.CreateBoxSetAsync(name, await Visible(ctx, q.Guids("ids")), q.Bool("isLocked") ?? false, ctx.RequestAborted);
        return Results.Json(new CollectionCreationResult { Id = boxSet.Id.N() }, AppJson.Default.CollectionCreationResult);
    }

    private static async Task<Collection?> BoxSet(HttpContext ctx, ICollectionManager collections) =>
        ctx.RouteGuid("collectionId") is { } id && await collections.GetAsync(id, ctx.RequestAborted) is { IsPlaylist: false } c ? c : null;

    private static async Task<IResult> AddToCollection(HttpContext ctx, ICollectionManager collections)
    {
        if (await BoxSet(ctx, collections) is not { } boxSet) return Results.NotFound("No collection exists with the supplied collectionId");
        await collections.AddAsync(boxSet.Id, await Visible(ctx, ctx.Request.Query.Guids("ids")), ct: ctx.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFromCollection(HttpContext ctx, ICollectionManager collections)
    {
        if (await BoxSet(ctx, collections) is not { } boxSet) return Results.NotFound("No collection exists with the supplied Id");
        await collections.RemoveAsync(boxSet.Id, ctx.Request.Query.Guids("ids"), ctx.RequestAborted);
        return Results.NoContent();
    }

    // ---- deleting ----

    private enum Deleted { Ok, NotFound, Forbidden }

    /// <summary>
    /// Jellyfin's DeleteItem for what this server can delete: playlists (their owner or an administrator) and collections (an
    /// administrator). Media files are never deleted from here; the clients do not offer it (CanDelete is false on them).
    /// </summary>
    private static async Task<Deleted> DeleteAsync(HttpContext ctx, Guid id, ICollectionManager collections, ItemRepository items)
    {
        var me = ctx.Auth().User;
        if (await collections.GetAsync(id, ctx.RequestAborted) is { } c)
        {
            var allowed = c.IsPlaylist ? me.IsAdmin || (c.OwnerId == me.Id) : ctx.Policy().EnableCollectionManagement;
            if (!allowed || !c.IsVisibleTo(me.Id) && !me.IsAdmin) return Deleted.Forbidden;
            await collections.DeleteAsync(id, ctx.RequestAborted);
            return Deleted.Ok;
        }
        return await items.GetAsync(id, ctx.RequestAborted) is null ? Deleted.NotFound : Deleted.Forbidden;
    }

    private static async Task<IResult> DeleteItem(HttpContext ctx, ICollectionManager collections, ItemRepository items)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        return await DeleteAsync(ctx, id, collections, items) switch
        {
            Deleted.Ok => Results.NoContent(),
            Deleted.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> DeleteItems(HttpContext ctx, ICollectionManager collections, ItemRepository items)
    {
        foreach (var id in ctx.Request.Query.Guids("ids"))
            if (await DeleteAsync(ctx, id, collections, items) == Deleted.Forbidden)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Results.NoContent();
    }
}
