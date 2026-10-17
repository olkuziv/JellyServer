using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Media.Contracts;

namespace JellyServer.Media;

/// <summary>
/// The files behind catalog items (main file and versions, .strm targets, streams, sidecar subtitles). Read only by playback and
/// item pages, so it sits apart from the browsing data: schema <c>media</c>, its own server with <c>ConnectionStrings:media</c>.
/// </summary>
public sealed class MediaModule : IModule
{
    public const string ModuleName = "media";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "media", 15, typeof(MediaModule).Assembly, "JellyServer.Media.Migrations."));
        services.AddSingleton<MediaSourceStore>();
        services.AddSingleton<IMediaSourceStore>(sp => sp.GetRequiredService<MediaSourceStore>());
        services.AddItemLinkedData<MediaSourceStore>();
    }
}
