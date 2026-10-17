using JellyServer.Catalog.Contracts;
using JellyServer.Scanning;

namespace JellyServer.Scanners.TvShows;

public sealed class SeriesFolderScanner(ScanContext ctx)
{
    private sealed record EpisodeFile(DirListing Dir, FileEntry File, int Season, Naming.EpisodeInfo Info);

    private sealed class SeasonFolder(int number)
    {
        public int Number { get; } = number;
        public DirListing? Dir { get; set; }
    }

    /// <summary>Every top-level folder of a TV library is one series, processed by one worker.</summary>
    public void ScanSeries(string dir, CancellationToken ct, DirListing? listing = null)
    {
        var sl = listing ?? ctx.TryRead(dir);
        if (sl is null) return;

        var episodes = new List<EpisodeFile>();
        var seasons = new SortedDictionary<int, SeasonFolder>();
        CollectEpisodes(sl, null, 0, episodes, seasons);
        if (episodes.Count == 0) return;
        var firstAdded = DateTime.UnixEpoch.AddSeconds(episodes.Min(e => e.File.MTime));

        // ---- Series ----
        var seriesId = ctx.RelId("series", dir);
        var sfp = new Fingerprint();
        var showNfo = sl.File("tvshow.nfo");
        sfp.Add("nfo", showNfo);
        var seriesImages = sl.ItemImages(sfp, null, folderLevel: true);
        var seriesSum = sfp.ToString();
        if (!ctx.IsUnchanged(seriesId, seriesSum))
        {
            var (title, year) = Naming.ParseMovieName(Path.GetFileName(dir));
            var series = new Item
            {
                Id = seriesId, Type = ItemTypes.Series, Name = title, ProductionYear = year, Path = dir, ProviderIds = Naming.ParseProviderIds(Path.GetFileName(dir)),
                DateCreated = firstAdded, Images = seriesImages,
            };
            if (showNfo is not null && ctx.ReadNfo(sl.Full(showNfo.Name)) is { } nfo)
            {
                nfo.ApplyTo(series);
                series.RuntimeTicks = 0;
                ImageRefs.MergeFrom(series.Images, nfo);
            }
            ctx.Emit(series, seriesSum, ct);
        }

        // ---- Seasons ----
        var seasonIds = new Dictionary<int, Guid>();
        foreach (var (n, folder) in seasons)
        {
            var id = ctx.RelId("season", dir, n.ToString());
            seasonIds[n] = id;
            var fp = new Fingerprint();
            fp.Add("n", n.ToString());
            fp.Add("series", seriesId.ToString("N")); // reparenting must re-save the row
            var images = new ItemImages();
            string[] prefixes = n == 0 ? [$"season{n:00}", $"season{n}", "season-specials"] : [$"season{n:00}", $"season{n}"];
            foreach (var p in prefixes)
            {
                images.Primary ??= sl.FindImage(fp, "p", p + "-poster", p);
                images.Thumb ??= sl.FindImage(fp, "t", p + "-landscape", p + "-thumb");
            }
            FileEntry? seasonNfo = null;
            var path = folder.Dir?.Path ?? dir;
            if (folder.Dir is { } sd)
            {
                images.Primary ??= sd.FindImage(fp, "sp", "poster", "folder", "cover");
                if (sd.FindImage(fp, "sb", "fanart", "backdrop") is { } bd) images.Backdrops.Add(bd);
                seasonNfo = sd.File("season.nfo");
                fp.Add("nfo", seasonNfo);
            }
            fp.Add("path", path);
            var sum = fp.ToString();
            if (ctx.IsUnchanged(id, sum)) continue;

            var season = new Item
            {
                Id = id, Type = ItemTypes.Season, Name = SeasonName(n), Path = path,
                ParentId = seriesId, SeriesId = seriesId, IndexNumber = n,
                DateCreated = firstAdded, Images = images,
            };
            if (seasonNfo is not null && ctx.ReadNfo(folder.Dir!.Full(seasonNfo.Name)) is { } nfo)
            {
                nfo.ApplyTo(season);
                if (nfo.Title is null) season.Name = SeasonName(n);
            }
            season.SortName = n.ToString("0000");
            ctx.Emit(season, sum, ct);
        }

        // ---- Episodes ----
        // The same episode in several files (resolutions, voice-overs) is one episode with versions.
        var episodeGroups = VideoVersions.ByKey(episodes, e => e.Info.Episode is { } n ? $"{e.Season}x{n}" : null, e => Path.GetFileNameWithoutExtension(e.File.Name));
        foreach (var group in episodeGroups)
        {
            var ef = group[0];
            var baseName = Path.GetFileNameWithoutExtension(ef.File.Name);
            var full = ef.Dir.Full(ef.File.Name);
            var fp = new Fingerprint();
            fp.Add("v", ef.File);
            var nfoEntry = ef.Dir.File(baseName + ".nfo");
            fp.Add("nfo", nfoEntry);
            var images = ef.Dir.EpisodeImages(fp, baseName);
            var subs = ef.Dir.Subtitles(fp, baseName);
            var versions = group.Skip(1).Select(a => VideoVersions.CreateVersion(ctx, "episode", a.Dir, a.File, fp)).ToList();
            fp.Add("s", ef.Season.ToString());
            // The row keeps its parents in the database: a regrouped series must rewrite it even if the file is untouched.
            fp.Add("parents", seriesId.ToString("N") + seasonIds[ef.Season].ToString("N"));

            var id = ctx.RelId("episode", full);
            var sum = fp.ToString();
            if (ctx.IsUnchanged(id, sum)) continue;

            var ep = new Item
            {
                Id = id, Type = ItemTypes.Episode, Path = full, Size = ef.File.Size,
                ParentId = seasonIds[ef.Season], SeasonId = seasonIds[ef.Season], SeriesId = seriesId,
                ParentIndexNumber = ef.Season, IndexNumber = ef.Info.Episode,
                Name = Naming.EpisodeTitle(ef.Info.Rest) ?? "",
                DateCreated = DateTime.UnixEpoch.AddSeconds(ef.File.MTime),
                Images = images, Subtitles = subs, Versions = versions.Count > 0 ? versions : null,
            };
            if (ef.File.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                ep.StreamUrl = ImageRefs.ReadStrm(full);
                ep.Size = 0;
            }
            ep.Container = Naming.ContainerFor(full, ep.StreamUrl);
            if (nfoEntry is not null && ctx.ReadNfo(ef.Dir.Full(nfoEntry.Name)) is { } nfo)
            {
                nfo.ApplyTo(ep);
                ImageRefs.MergeFrom(ep.Images, nfo);
                if (int.TryParse(nfo.EpisodeNumber, out var num)) ep.IndexNumber = num;
            }
            if (string.IsNullOrEmpty(ep.Name))
                ep.Name = ep.IndexNumber is { } i ? $"Episode {i}" : baseName;
            // Episodes sort by number, never by title.
            ep.SortName = $"{ef.Season:0000}{ep.IndexNumber ?? 99999:00000}";
            foreach (var version in versions) version.Fingerprint = sum;
            ctx.Emit(ep, sum, ct);
        }
    }

    /// <summary>Walks a series folder (max depth 3), assigning season numbers from names or folders.</summary>
    private void CollectEpisodes(DirListing l, int? folderSeason, int depth, List<EpisodeFile> output, SortedDictionary<int, SeasonFolder> seasons)
    {
        foreach (var v in l.Videos())
        {
            var info = Naming.ParseEpisode(Path.GetFileNameWithoutExtension(v.Name));
            var season = info.Season ?? folderSeason ?? 1;
            if (!seasons.TryGetValue(season, out var folder)) seasons[season] = folder = new SeasonFolder(season);
            if (depth > 0 && folderSeason == season) folder.Dir ??= l;
            output.Add(new EpisodeFile(l, v, season, info));
        }
        if (depth >= 3) return;
        foreach (var sub in l.SubDirs())
        {
            if (ctx.TryRead(sub) is not { } child) continue;
            var fs = Naming.ParseSeasonDir(Path.GetFileName(sub)) ?? folderSeason;
            CollectEpisodes(child, fs, depth + 1, output, seasons);
        }
    }

    /// <summary>
    /// In a mixed library a folder is a series when it has explicit season folders or SxxExx / NxNN files
    /// (bare numbers are not enough: "12 Angry Men" is a movie); otherwise it holds movies.
    /// </summary>
    public static bool LooksLikeSeries(DirListing l) =>
        l.Dirs.Any(d => Naming.HasSeasonMarker(Path.GetFileName(d)))
        || l.Videos().Any(v => Naming.ParseEpisode(Path.GetFileNameWithoutExtension(v.Name)) is { Season: not null, Episode: not null });

    private static string SeasonName(int n) => n == 0 ? "Specials" : $"Season {n}";
}
