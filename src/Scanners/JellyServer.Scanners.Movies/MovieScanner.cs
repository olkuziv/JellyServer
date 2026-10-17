using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Scanning;

namespace JellyServer.Scanners.Movies;

/// <summary>Movie libraries: every video or .strm in any folder is a movie.</summary>
public sealed class MovieScanner : ILibraryScanner
{
    public IReadOnlyCollection<string> CollectionTypes { get; } = [LibraryTypes.Movies];

    public async Task ScanRootAsync(ScanContext context, string root, CancellationToken ct)
    {
        if (context.OpenRoot(root) is not { } listing) return;
        var movies = new MovieFolderScanner(context);
        // Reuse the root listing: it can hold tens of thousands of folders.
        movies.ScanFiles(listing, ct);
        await ParallelWalker.WalkAsync(listing.SubDirs(), context.Workers, dir => movies.ScanDir(dir, ct), ct);
    }
}

public sealed class MoviesScannerModule : IModule
{
    public string Name => "scanner-movies";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddSingleton<ILibraryScanner, MovieScanner>();
}