using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Scanners.Movies;
using JellyServer.Scanners.TvShows;
using JellyServer.Scanning;

namespace JellyServer.Scanners.Mixed;

/// <summary>
/// "Mixed movies and shows" libraries (no collection type in Jellyfin): loose files of the root are movies, and every
/// folder is decided on its own - a series when it looks like one, otherwise a folder holding movies.
/// </summary>
public sealed class MixedScanner : ILibraryScanner
{
    public IReadOnlyCollection<string> CollectionTypes { get; } = [LibraryTypes.Mixed];

    public async Task ScanRootAsync(ScanContext context, string root, CancellationToken ct)
    {
        if (context.OpenRoot(root) is not { } listing) return;
        var movies = new MovieFolderScanner(context);
        var series = new SeriesFolderScanner(context);
        movies.ScanFiles(listing, ct);
        await ParallelWalker.WalkAsync(listing.SubDirs(), context.Workers, dir =>
        {
            if (context.TryRead(dir) is not { } l) return [];
            if (SeriesFolderScanner.LooksLikeSeries(l))
            {
                series.ScanSeries(dir, ct, l);
                return [];
            }
            movies.ScanFiles(l, ct);
            return l.SubDirs();
        }, ct);
    }
}

public sealed class MixedScannerModule : IModule
{
    public string Name => "scanner-mixed";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddSingleton<ILibraryScanner, MixedScanner>();
}