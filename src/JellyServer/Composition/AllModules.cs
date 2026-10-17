using JellyServer.Catalog;
using JellyServer.Collections;
using JellyServer.Identity;
using JellyServer.Kernel;
using JellyServer.Media;
using JellyServer.Metadata;
using JellyServer.People;
using JellyServer.Providers.Tmdb;
using JellyServer.Scanners.Mixed;
using JellyServer.Scanners.Movies;
using JellyServer.Scanners.TvShows;
using JellyServer.Settings;
using JellyServer.Tasks;
using JellyServer.UserData;

namespace JellyServer.Composition;

/// <summary>Every module the host loads. Data modules own a schema (and, if configured, their own database); scanner modules add library types, the metadata module orchestrates internet providers (TMDB, ...).</summary>
public static class AllModules
{
    public static readonly IModule[] Core =
    [
        new IdentityModule(), new UserDataModule(), new CatalogModule(), new MediaModule(), new PeopleModule(), new CollectionsModule(),
        new SettingsModule(), new TasksModule(),
        new MoviesScannerModule(), new TvShowsScannerModule(), new MixedScannerModule(),
        new MetadataModule(), new TmdbModule(),
    ];
}
