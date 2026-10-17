using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity;
using JellyServer.UserData;

namespace JellyServer.Api.Endpoints;

/// <summary>Artwork. Anonymous like Jellyfin: image URLs are embedded in &lt;img&gt; tags.</summary>
public static class ImageEndpoints
{
    private static readonly string[] Types = ["Primary", "Backdrop", "Logo", "Thumb", "Banner", .. ItemImages.OtherTypes];

    public static void Map(WebApplication app)
    {
        app.MapGet("/Items/{id}/Images", ImageInfos);
        app.MapMethods("/Items/{id}/Images/{type}", ["GET", "HEAD"], Image);
        app.MapMethods("/Items/{id}/Images/{type}/{index:int}", ["GET", "HEAD"], Image);

        // Dashboard "Upload image" / "Delete image"; the target can be an item or a library.
        var admin = app.MapGroup("/Items/{id}/Images").RequireAdmin();
        admin.MapPost("/{type}", Upload);
        admin.MapPost("/{type}/{index:int}", Upload);
        admin.MapDelete("/{type}", Delete);
        admin.MapDelete("/{type}/{index:int}", Delete);
    }

    /// <summary>The thing images belong to: an item, or a library (shown to clients as a CollectionFolder).</summary>
    private sealed record Target(ItemImages Images, string Fingerprint, Func<ItemImages, CancellationToken, Task> Save);

    private static async Task<Target?> Resolve(Guid id, ItemRepository items, LibraryRegistry libs, LibraryStore store, CancellationToken ct)
    {
        if (libs.Find(id) is { } lib) return new Target(lib.Images, "", (img, c) => libs.SetImagesAsync(lib, img, c));
        if (await items.GetAsync(id, ct) is not { } item) return null;
        return new Target(item.Images, item.Fingerprint, async (img, c) =>
        {
            await store.SetItemImagesAsync(id, img, c);
            await items.InvalidateAsync(id);
        });
    }

    private static void Set(ItemImages images, string type, int index, string? path)
    {
        switch (type)
        {
            case "Primary": images.Primary = path; break;
            case "Logo": images.Logo = path; break;
            case "Thumb": images.Thumb = path; break;
            case "Banner": images.Banner = path; break;
            case "Backdrop":
                if (path is null) { if (index >= 0 && index < images.Backdrops.Count) images.Backdrops.RemoveAt(index); }
                else images.Backdrops.Add(path);
                break;
            default:
                if (path is null) images.Other?.Remove(type);
                else (images.Other ??= [])[type] = path;
                break;
        }
    }

    private static ItemImages Copy(ItemImages i) =>
        new() { Primary = i.Primary, Logo = i.Logo, Thumb = i.Thumb, Banner = i.Banner, Backdrops = [.. i.Backdrops],
            Other = i.Other is null ? null : new(i.Other) };

    /// <summary>
    /// POST /Items/{id}/Images/{type}. jellyfin-web sends the picture base64-encoded in the body (Content-Type is the picture's);
    /// plain binary bodies are accepted too. Backdrops are added; the other types are replaced.
    /// </summary>
    private static async Task<IResult> Upload(HttpContext ctx, ItemRepository items, LibraryRegistry libs, LibraryStore store, ImageService images)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        var type = Types.FirstOrDefault(t => t.Equals(ctx.Request.RouteValues["type"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        if (type is null) return Results.BadRequest();
        var ext = ImageService.UploadExtension(ctx.Request.ContentType);
        if (ext is null) return Results.BadRequest("Unsupported image type");
        var ct = ctx.RequestAborted;
        if (await Resolve(id, items, libs, store, ct) is not { } target) return Results.NotFound();

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ct);
        var data = ms.ToArray();
        if (data.Length == 0) return Results.BadRequest();
        var binary = data is [0xFF, 0xD8, ..] or [0x89, (byte)'P', ..] or [(byte)'G', (byte)'I', (byte)'F', ..] or [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..];
        if (!binary)
        {
            try { data = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(data).Trim()); }
            catch (FormatException) { return Results.BadRequest("Invalid image data"); }
        }

        string path;
        try { path = await images.SaveUploadAsync(id, type, ext, data, ct); }
        catch (InvalidDataException ex) { return Results.BadRequest(ex.Message); }
        var next = Copy(target.Images);
        var old = type == "Backdrop" ? null : next.Get(type, 0);
        Set(next, type, 0, path);
        await target.Save(next, ct);
        images.DeleteUpload(old);
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(HttpContext ctx, ItemRepository items, LibraryRegistry libs, LibraryStore store, ImageService images)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        var type = Types.FirstOrDefault(t => t.Equals(ctx.Request.RouteValues["type"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        if (type is null) return Results.BadRequest();
        var index = int.TryParse(ctx.Request.RouteValues["index"]?.ToString(), out var i) ? i : 0;
        var ct = ctx.RequestAborted;
        if (await Resolve(id, items, libs, store, ct) is not { } target) return Results.NotFound();

        var next = Copy(target.Images);
        var old = next.Get(type, index);
        if (old is null) return Results.NoContent();
        Set(next, type, index, null);
        await target.Save(next, ct);
        images.DeleteUpload(old);
        return Results.NoContent();
    }

    private static async Task<IResult> Image(HttpContext ctx, ItemRepository items, LibraryRegistry libs, ImageService images)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        var type = Types.FirstOrDefault(t => t.Equals(ctx.Request.RouteValues["type"]?.ToString(), StringComparison.OrdinalIgnoreCase));
        if (type is null) return Results.NotFound();
        var index = int.TryParse(ctx.Request.RouteValues["index"]?.ToString(), out var i) ? i : 0;

        var libImages = libs.Find(id)?.Images;
        var item = libImages is null ? await items.GetAsync(id, ctx.RequestAborted) : null;
        var source = (libImages ?? item?.Images)?.Get(type, index);
        if (source is null) return Results.NotFound();
        var fingerprint = item?.Fingerprint ?? "";

        var q = ctx.Request.Query;
        var path = await images.GetAsync(source, new ImageService.ImageRequest(
            q.Int("maxWidth") ?? q.Int("width"), q.Int("maxHeight") ?? q.Int("height"),
            q.Int("fillWidth"), q.Int("fillHeight"), q.Int("quality"), type), ctx.RequestAborted);
        if (path is null) return Results.NotFound();

        // Tags are content hashes: tagged URLs can be cached by clients forever.
        var tag = DtoBuilder.ImageTag(source, fingerprint);
        ctx.Response.Headers.CacheControl = q.Str("tag") == tag ? "public, max-age=31536000, immutable" : "public, max-age=3600";
        ctx.Response.Headers.ETag = $"\"{tag}\"";
        if (ctx.Request.Headers.IfNoneMatch.ToString() == $"\"{tag}\"") return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.File(path, ImageService.ContentType(path), enableRangeProcessing: false);
    }

    private static async Task<IResult> ImageInfos(HttpContext ctx, ItemRepository items, LibraryRegistry libs, LibraryStore store)
    {
        if (ctx.RouteGuid("id") is not { } id || await Resolve(id, items, libs, store, ctx.RequestAborted) is not { } target) return Results.NotFound();
        var (images, fingerprint) = (target.Images, target.Fingerprint);
        var list = new List<ImageInfoDto>();
        foreach (var t in Types.Where(t => t != "Backdrop"))
            if (images.Get(t, 0) is { } src) list.Add(new ImageInfoDto { ImageType = t, ImageTag = DtoBuilder.ImageTag(src, fingerprint), Path = src });
        for (var b = 0; b < images.Backdrops.Count; b++)
            list.Add(new ImageInfoDto { ImageType = "Backdrop", ImageIndex = b, ImageTag = DtoBuilder.ImageTag(images.Backdrops[b], fingerprint), Path = images.Backdrops[b] });
        return Results.Json(list, AppJson.Default.ListImageInfoDto);
    }
}
