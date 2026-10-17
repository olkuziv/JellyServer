using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Scanning;

namespace JellyServer.Scanners.TvShows;

/// <summary>TV libraries: every top-level folder is one series, processed by one worker.</summary>
public sealed class SeriesScanner : ILibraryScanner
{
    public IReadOnlyCollection<string> CollectionTypes { get; } = [LibraryTypes.TvShows];

    public async Task ScanRootAsync(ScanContext context, string root, CancellationToken ct)
    {
        if (context.OpenRoot(root) is not { } listing) return;
        var series = new SeriesFolderScanner(context);
        await ParallelWalker.WalkAsync(listing.SubDirs(), context.Workers, dir =>
        {
            series.ScanSeries(dir, ct);
            return [];
        }, ct);
    }
}

public sealed class TvShowsScannerModule : IModule
{
    public string Name => "scanner-tvshows";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddSingleton<ILibraryScanner, SeriesScanner>();
}