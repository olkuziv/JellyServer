using JellyServer.Catalog.Contracts;

namespace JellyServer.Scanning;

/// <summary>File-system helpers scanners share on top of <see cref="ScanContext"/>.</summary>
public static class ScanContextExtensions
{
    /// <summary>
    /// Starts scanning a root folder and lists it. Returns null (and forbids deletions after this scan) when the root
    /// cannot be read or is empty: an empty root almost always means an unmounted share, not "delete everything".
    /// </summary>
    public static DirListing? OpenRoot(this ScanContext ctx, string root)
    {
        ctx.SetRoot(root);
        DirListing listing;
        try
        {
            listing = DirListing.Read(ctx.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log.LogWarning("Library root {Root} unreadable, deletions disabled for this scan: {Error}", root, ex.Message);
            ctx.MarkRootError();
            return null;
        }
        if (listing.Files.Count == 0 && listing.Dirs.Count == 0)
        {
            ctx.Log.LogWarning("Library root {Root} is empty, deletions disabled for this scan", root);
            ctx.MarkRootError();
            return null;
        }
        ctx.CountDirectory(listing.Files.Count);
        return listing;
    }

    /// <summary>Lists a folder and counts it; null (with a warning) when it cannot be read.</summary>
    public static DirListing? TryRead(this ScanContext ctx, string dir)
    {
        try
        {
            var l = DirListing.Read(dir);
            ctx.CountDirectory(l.Files.Count);
            return l;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log.LogWarning("Cannot read directory {Dir}: {Error}", dir, ex.Message);
            return null;
        }
    }

    public static Nfo? ReadNfo(this ScanContext ctx, string path) => Nfo.Read(path, ctx.Log);
}