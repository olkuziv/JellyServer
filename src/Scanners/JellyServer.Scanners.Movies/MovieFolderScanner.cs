using JellyServer.Catalog.Contracts;
using JellyServer.Scanning;

namespace JellyServer.Scanners.Movies;

public sealed class MovieFolderScanner(ScanContext ctx)
{
    /// <summary>Turns every video/.strm in a folder into a Movie and returns its subfolders.</summary>
    public IEnumerable<string> ScanDir(string dir, CancellationToken ct)
    {
        var l = ctx.TryRead(dir);
        if (l is null) return [];
        ScanFiles(l, ct);
        return l.SubDirs();
    }

    public void ScanFiles(DirListing l, CancellationToken ct)
    {
        var dir = l.Path;
        var videos = l.Videos();
        // Files named after their folder ("Movie (2020) - 1080p.mkv") are versions of one movie.
        var groups = dir == ctx.Root ? videos.Select(v => new List<FileEntry> { v }).ToList() : VideoVersions.ForMovieFolder(Path.GetFileName(dir), videos);
        // Folder-level artwork and movie.nfo belong to the movie only when it is alone in a non-root folder.
        var single = groups.Count == 1 && dir != ctx.Root;

        foreach (var group in groups)
        {
            var v = group[0];
            var baseName = Path.GetFileNameWithoutExtension(v.Name);
            var full = l.Full(v.Name);
            var fp = new Fingerprint();
            fp.Add("v", v);
            var nfoEntry = l.File(baseName + ".nfo") ?? (single ? l.File("movie.nfo") : null);
            fp.Add("nfo", nfoEntry);
            var images = l.ItemImages(fp, baseName, single);
            var subs = l.Subtitles(fp, baseName);
            var versions = group.Skip(1).Select(f => VideoVersions.CreateVersion(ctx, "movie", l, f, fp)).ToList();

            var id = ctx.RelId("movie", full);
            var sum = fp.ToString();
            if (ctx.IsUnchanged(id, sum)) continue;

            var (title, year) = Naming.ParseMovieName(single ? Path.GetFileName(dir) : baseName);
            var it = new Item
            {
                Id = id, Type = ItemTypes.Movie, Name = title, ProductionYear = year, Path = full,
                Size = v.Size, DateCreated = DateTime.UnixEpoch.AddSeconds(v.MTime),
                Images = images, Subtitles = subs, Versions = versions.Count > 0 ? versions : null,
                ProviderIds = Naming.ParseProviderIds(baseName, single ? Path.GetFileName(dir) : null),
            };
            if (v.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                it.StreamUrl = ImageRefs.ReadStrm(full);
                it.Size = 0;
            }
            it.Container = Naming.ContainerFor(full, it.StreamUrl);
            if (nfoEntry is not null && ctx.ReadNfo(l.Full(nfoEntry.Name)) is { } nfo)
            {
                nfo.ApplyTo(it);
                ImageRefs.MergeFrom(it.Images, nfo);
            }
            foreach (var version in versions) version.Fingerprint = sum;
            ctx.Emit(it, sum, ct);
        }
    }
}
