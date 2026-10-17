using System.IO.Compression;
using JellyServer.Api;
using JellyServer.Api.Endpoints;
using JellyServer.Configuration;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Scanning;
using JellyServer.Composition;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.Settings;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.Settings;
using JellyServer.UserData;
using JellyServer.Transcoding;
using JellyServer.Transcoding.Pipelines;
using JellyServer.Transcoding.Amf;
using JellyServer.Transcoding.Nvenc;
using JellyServer.Transcoding.Qsv;
using JellyServer.Transcoding.Rkmpp;
using JellyServer.Transcoding.SvtAv1;
using JellyServer.Transcoding.V4l2m2m;
using JellyServer.Transcoding.Vaapi;
using JellyServer.Transcoding.VideoToolbox;
using JellyServer.Transcoding.X264;
using JellyServer.Transcoding.X265;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Npgsql;
using JellyServer.Kernel.Caching;
using JellyServer.Settings.Contracts;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: false);
// Settings saved on the Jellyfin dashboard: added last, so they override files and environment variables.
var dashboard = new DashboardConfigurationSource();
((IConfigurationBuilder)builder.Configuration).Add(dashboard);

var options = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
options.Validate();
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));

// ---- logging: JSON lines in containers, readable text in development ----
builder.Logging.ClearProviders();
if (builder.Configuration["Logging:Format"] == "text" || builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
else
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; o.UseUtcTimestamp = true; });

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(options.Port);
    k.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
    k.AddServerHeader = false;
});

// ---- infrastructure ----
// Each module owns a schema and may own a server: ConnectionStrings:<Module> (identity, userdata, catalog, settings)
// overrides the shared ConnectionStrings:Postgres.
builder.Services.AddSingleton(new DatabaseSettings(options.SlowQueryMs));
builder.Services.AddModules(builder.Configuration, AllModules.Core);
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
    RedisCache.ConnectAsync(redisConnection, sp.GetRequiredService<ILogger<RedisCache>>(), CancellationToken.None).GetAwaiter().GetResult());
builder.Services.AddSingleton<RedisCache>();
builder.Services.AddMemoryCache(o => o.SizeLimit = null);
builder.Services.AddHttpClient("upstream", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 64,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
builder.Services.AddHttpClient("images", c => c.Timeout = TimeSpan.FromSeconds(30));

// ---- domain services ----
builder.Services.AddSingleton(dashboard.Provider);
builder.Services.AddSingleton<DashboardSettingsSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardSettingsSync>());
builder.Services.AddSingleton<ServerIdentity>();
builder.Services.AddSingleton<JellyServer.Localization.LocalizationService>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<DtoBuilder>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<StreamProxy>();
builder.Services.AddSingleton<Metrics>();
builder.Services.AddSingleton<JellyServer.Transcoding.MediaProbe>();
builder.Services.AddSingleton<SubtitleExtractor>();
// Video pipelines: stream copy is built in, every encoder is a separate module. Registration order is the
// default preference (hardware first); encoders whose startup probe fails are skipped.
var transcoding = builder.Configuration.GetSection($"{ServerOptions.Section}:Transcoding");
builder.Services.AddSingleton<IVideoPipeline, CopyPipeline>();
builder.Services.AddNvencPipeline(transcoding);
builder.Services.AddQsvPipeline(transcoding);
builder.Services.AddVaapiPipeline(transcoding);
builder.Services.AddAmfPipeline(transcoding);
builder.Services.AddVideoToolboxPipeline(transcoding);
builder.Services.AddRkmppPipeline(transcoding);
builder.Services.AddV4l2m2mPipeline(transcoding);
builder.Services.AddX264Pipeline(transcoding);
builder.Services.AddX265Pipeline(transcoding);
builder.Services.AddSvtAv1Pipeline(transcoding);
builder.Services.AddSingleton<PipelineSelector>();
builder.Services.AddSingleton<JellyServer.Transcoding.TranscodeManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JellyServer.Transcoding.TranscodeManager>());
builder.Services.AddHostedService<CacheGenerationSync>();
builder.Services.AddHostedService<ImageCacheJanitor>();
builder.Services.AddHostedService<DiscoveryService>();

// ---- HTTP ----
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default);
    o.SerializerOptions.PropertyNamingPolicy = null;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ["application/json", "text/plain", "text/vtt", "application/vnd.apple.mpegurl",
        "text/html", "text/css", "text/javascript", "application/javascript", "image/svg+xml"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
// The Tizen app runs jellyfin-web from a packaged origin, so CORS must be wide open.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("Content-Range", "Content-Length", "Accept-Ranges")
    .SetPreflightMaxAge(TimeSpan.FromHours(24))));
builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = false);

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();

// ---- startup: database, libraries, admin account ----
using (var startupCts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
{
    await app.Services.GetRequiredService<DatabaseSet>().MigrateAllAsync(startupCts.Token);
    await app.Services.GetRequiredService<LibraryRegistry>().ReloadAsync(startupCts.Token);
    await app.Services.GetRequiredService<RedisCache>().RefreshGenerationAsync();
    // Dashboard settings first: the pipeline probe must see the saved hardware type and devices.
    await app.Services.GetRequiredService<DashboardSettingsSync>().LoadAsync(startupCts.Token);
    await app.Services.GetRequiredService<PipelineSelector>().InitializeAsync(startupCts.Token);
    // Installs from before the startup wizard already have accounts: they are set up, do not send them through it.
    var serverConfig = app.Services.GetRequiredService<IServerConfiguration>();
    if (!await serverConfig.IsStartupCompletedAsync(startupCts.Token))
    {
        if (await app.Services.GetRequiredService<UserRepository>().AnyAsync()) await serverConfig.CompleteStartupAsync(startupCts.Token);
        else log.LogWarning("Startup wizard not completed: open the web client to create the first user and add libraries");
    }
}

app.UseMiddleware<PathNormalizationMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseCors();
app.UseResponseCompression();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

WebClient.Map(app, options, log);
SystemEndpoints.Map(app);
TaskEndpoints.Map(app);
UserEndpoints.Map(app);
ItemEndpoints.Map(app);
ImageEndpoints.Map(app);
PlaybackEndpoints.Map(app);
HlsEndpoints.Map(app);
FallbackFontEndpoints.Map(app);
StartupEndpoints.Map(app);
LibraryStructureEndpoints.Map(app);
MetadataEndpoints.Map(app);
EnvironmentEndpoints.Map(app);

log.LogInformation("{Name} {Version} listening on :{Port} with {Libraries} libraries (strm mode: {StrmMode})",
    options.Name, options.Version, options.Port, app.Services.GetRequiredService<LibraryRegistry>().All.Count, options.Playback.StrmMode);
await app.RunAsync();
