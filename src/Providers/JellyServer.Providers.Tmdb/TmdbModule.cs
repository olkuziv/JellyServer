using JellyServer.Kernel;
using JellyServer.Metadata.Contracts;
using Microsoft.Extensions.Options;

namespace JellyServer.Providers.Tmdb;

/// <summary>Registers TMDB as a metadata provider. Needs <c>TMDB_API_KEY</c>; without it the provider stays off.</summary>
public sealed class TmdbModule : IModule
{
    public string Name => "provider-tmdb";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TmdbOptions>(configuration.GetSection(TmdbOptions.Section));
        services.PostConfigure<TmdbOptions>(o => o.ApiKey ??= configuration["TMDB_API_KEY"]);
        services.AddSingleton(sp =>
        {
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JellyServer");
            return new TmdbClient(http, sp.GetRequiredService<IOptions<TmdbOptions>>(), sp.GetRequiredService<ILogger<TmdbClient>>());
        });
        services.AddSingleton<IMetadataProvider, TmdbMetadataProvider>();
    }
}