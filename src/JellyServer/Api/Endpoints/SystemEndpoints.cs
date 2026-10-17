using JellyServer.Localization;
using JellyServer.Settings;
using System.Security.Cryptography;
using System.Text.Json;
using JellyServer.Configuration;
using JellyServer.Kernel.Data;
using JellyServer.Scanning;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Transcoding;
using Microsoft.Extensions.Options;
using Npgsql;
using JellyServer.Kernel.Caching;
using JellyServer.Catalog.Contracts;
using JellyServer.Settings.Contracts;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Api.Endpoints;

public sealed record ServerStatus(
    string Version, long ItemCacheHits, long ItemCacheMisses, long CacheGeneration, bool RedisConnected,
    Dictionary<string, int> ItemCounts, ScanStatus Scan, List<PipelineStatus> VideoPipelines);

/// <summary>System info, branding, localization and the many "nothing here" endpoints clients probe.</summary>
public static class SystemEndpoints
{
    public static void Map(WebApplication app)
    {
        var anon = app.MapGroup("");
        anon.MapGet("/System/Info/Public", PublicInfo);
        anon.MapMethods("/System/Ping", ["GET", "POST"], () => Results.Text("\"Jellyfin Server\"", "application/json"));
        anon.MapGet("/Branding/Configuration", async (INamedConfigurationStore store, CancellationToken ct) =>
            await store.GetAsync("branding", ct) is { } json ? Results.Text(json, "application/json") : Results.Json(new BrandingOptions(), AppJson.Default.BrandingOptions));
        anon.MapGet("/Branding/Css", BrandingCss);
        anon.MapGet("/Branding/Css.css", BrandingCss);
        anon.MapGet("/QuickConnect/Enabled", () => Results.Json(false, AppJson.Default.Boolean));
        anon.MapGet("/Localization/Cultures", Cultures);
        anon.MapGet("/Localization/Options", LocalizationOptions);
        anon.MapGet("/Localization/Countries", Countries);
        anon.MapGet("/Localization/ParentalRatings", ParentalRatings);
        anon.MapGet("/health", Health);
        anon.MapGet("/metrics", MetricsText);

        var authed = app.MapGroup("").RequireToken();
        authed.MapGet("/System/Info", SystemInfo);
        authed.MapGet("/System/Endpoint", () => Results.Json(new EndpointInfo { IsLocal = false, IsInNetwork = true }, AppJson.Default.EndpointInfo));
        authed.MapGet("/System/Configuration", GetServerConfiguration);
        authed.MapGet("/System/Configuration/{key}", GetNamedConfiguration);
        authed.MapGet("/System/ActivityLog/Entries", EmptyResult);
        authed.MapGet("/System/Logs", EmptyList);
        authed.MapGet("/Plugins", EmptyList);
        authed.MapGet("/Packages", EmptyList);
        authed.MapGet("/web/ConfigurationPages", EmptyList);
        authed.MapGet("/Auth/Providers", EmptyList);
        authed.MapGet("/Auth/PasswordResetProviders", EmptyList);
        authed.MapGet("/Notifications/{userId}", EmptyResult);
        authed.MapGet("/Playback/BitrateTest", BitrateTest);
        authed.MapGet("/LiveTv/Info", () => Results.Json(new LiveTvInfo(), AppJson.Default.LiveTvInfo));
        foreach (var path in new[]
                 {
                     "/LiveTv/Channels", "/LiveTv/Programs/Recommended", "/LiveTv/Recordings", "/Channels", "/Studios",
                     "/Persons", "/Artists", "/MusicGenres", "/Trailers", "/Shows/Upcoming", "/Playlists/{id}/Items",
                     "/MediaSegments/{id}", "/Items/{id}/CriticReviews",
                 })
            authed.MapGet(path, EmptyResult);
        authed.MapGet("/SyncPlay/List", EmptyList);
        authed.MapGet("/Movies/Recommendations", EmptyList);

        var admin = app.MapGroup("").RequireAdmin();
        admin.MapGet("/JellyServer/Status", Status);
        admin.MapPost("/System/Configuration", SaveServerConfiguration);
        admin.MapPost("/System/Configuration/{key}", SaveNamedConfiguration);
        admin.MapPost("/JellyServer/Scan", (ITaskManager tasks) => tasks.Start(ScanLibraryTask.TaskId) ? Results.Accepted() : Results.Conflict("scan already running"));
        admin.MapPost("/Library/Refresh", (ITaskManager tasks) => { tasks.Start(ScanLibraryTask.TaskId); return Results.NoContent(); });
    }

    public static IResult EmptyList() => Results.Json(new List<object>(), AppJson.Default.ListObject);
    public static IResult EmptyResult() => Results.Json(QueryResult<BaseItemDto>.Empty, AppJson.Default.QueryResultBaseItemDto);

    private static string LocalAddress(HttpContext ctx, ServerOptions o) =>
        o.PublicUrl ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    private static async Task<IResult> PublicInfo(HttpContext ctx, IOptions<ServerOptions> o, ServerIdentity id, IServerConfiguration config) =>
        Results.Json(new PublicSystemInfo
        {
            LocalAddress = LocalAddress(ctx, o.Value), ServerName = await config.ServerNameAsync(ctx.RequestAborted) ?? o.Value.Name,
            Version = o.Value.Version, Id = id.ServerId,
            StartupWizardCompleted = await config.IsStartupCompletedAsync(ctx.RequestAborted),
        }, AppJson.Default.PublicSystemInfo);

    private static async Task<IResult> SystemInfo(HttpContext ctx, IOptions<ServerOptions> o, ServerIdentity id, IServerConfiguration config) =>
        Results.Json(new SystemInfo
        {
            LocalAddress = LocalAddress(ctx, o.Value), ServerName = await config.ServerNameAsync(ctx.RequestAborted) ?? o.Value.Name,
            Version = o.Value.Version, Id = id.ServerId,
            StartupWizardCompleted = await config.IsStartupCompletedAsync(ctx.RequestAborted),
            WebSocketPortNumber = o.Value.Port, ProgramDataPath = o.Value.DataDir, CachePath = Path.Combine(o.Value.DataDir, "cache"),
            SystemArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        }, AppJson.Default.SystemInfo);

    private static IResult Cultures(LocalizationService l) => Results.Bytes(l.CulturesJson, "application/json");

    private static IResult Countries(LocalizationService l) => Results.Bytes(l.CountriesJson, "application/json");

    private static IResult LocalizationOptions(LocalizationService l) => Results.Bytes(l.OptionsJson, "application/json");

    private static async Task<IResult> ParentalRatings(LocalizationService l, IServerConfiguration config, CancellationToken ct) =>
        Results.Bytes(l.ParentalRatingsJson((await config.GetAsync(ct))["MetadataCountryCode"]?.GetValue<string>()), "application/json");

    /// <summary>jellyfin-web measures bandwidth by downloading Size random bytes.</summary>
    private static async Task BitrateTest(HttpContext ctx)
    {
        var size = Math.Clamp(ctx.Request.Query.Int("size") ?? 102400, 1, 100_000_000);
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = size;
        var buffer = RandomNumberGenerator.GetBytes(Math.Min(size, 64 * 1024));
        for (var sent = 0; sent < size; sent += buffer.Length)
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, Math.Min(buffer.Length, size - sent)), ctx.RequestAborted);
    }

    private static async Task<IResult> Health(DatabaseSet databases, RedisCache cache)
    {
        foreach (var db in databases.All)
        {
            try
            {
                await using var conn = await db.DataSource.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                return Results.Problem($"postgres ({db.Module}): {ex.Message}", statusCode: 503);
            }
        }
        return Results.Text(cache.IsConnected ? "ok" : "ok (redis degraded)");
    }

    private static IResult MetricsText(Metrics metrics, RedisCache cache, ScanCoordinator scanner)
    {
        var (hits, misses) = cache.Stats;
        return Results.Text(metrics.Render([
            ("cache_hits_total", hits), ("cache_misses_total", misses),
            ("cache_generation", cache.Generation), ("scan_running", scanner.IsRunning ? 1 : 0),
            ("gc_heap_bytes", GC.GetTotalMemory(false)),
        ]), "text/plain; version=0.0.4");
    }

    // ---- dashboard configuration (Jellyfin's ConfigurationController) ----


    private static readonly Dictionary<string, object> DefaultServerConfiguration = new()
    {
        ["EnableMetrics"] = true, ["IsStartupWizardCompleted"] = false, ["UICulture"] = "en-US",
        ["MinResumePct"] = 5, ["MaxResumePct"] = 90, ["MinResumeDurationSeconds"] = 300,
    };

    private static async Task<IResult> GetServerConfiguration(IServerConfiguration config, CancellationToken ct) =>
        await config.GetAsync(ct) is { Count: > 0 } doc
            ? SetupAccess.Json(doc)
            : Results.Json(DefaultServerConfiguration, AppJson.Default.DictionaryStringObject);

    private static async Task<IResult> SaveServerConfiguration(HttpContext ctx, IServerConfiguration config)
    {
        if (await ReadObjectAsync(ctx) is not { } body) return Results.BadRequest("Body must be a JSON object");
        await config.ReplaceAsync(body, ctx.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>
    /// "encoding" is applied to transcoding (see <see cref="EncodingSettings"/>); other keys (metadata, network,
    /// branding, …) are stored as sent, so every dashboard page can save and reload its settings.
    /// </summary>
    private static async Task<IResult> GetNamedConfiguration(string key, INamedConfigurationStore store, IOptionsMonitor<ServerOptions> options,
        PipelineSelector pipelines, TranscodeManager transcodes, CancellationToken ct)
    {
        key = key.ToLowerInvariant();
        var json = await store.GetAsync(key, ct);
        if (key != EncodingSettings.Key)
            return json is null ? Results.Json(new Dictionary<string, object>(), AppJson.Default.DictionaryStringObject) : Results.Text(json, "application/json");

        var t = options.CurrentValue.Transcoding;
        var dto = json is null ? EncodingSettings.FromOptions(t, pipelines.ActiveHardware, transcodes.TempRoot) : EncodingSettings.Deserialize(json);
        dto.EncoderAppPath = dto.EncoderAppPathDisplay = t.FfmpegPath;
        if (string.IsNullOrWhiteSpace(dto.TranscodingTempPath)) dto.TranscodingTempPath = transcodes.TempRoot;
        return Results.Text(EncodingSettings.Serialize(dto), "application/json");
    }

    private static async Task<IResult> SaveNamedConfiguration(string key, HttpContext ctx, INamedConfigurationStore store, DashboardSettingsSync sync,
        IOptionsMonitor<ServerOptions> options, PipelineSelector pipelines, TranscodeManager transcodes, ILogger<DashboardSettingsSync> log)
    {
        key = key.ToLowerInvariant();
        if (await ReadObjectAsync(ctx) is not { } body) return Results.BadRequest("Body must be a JSON object");
        if (key != EncodingSettings.Key)
        {
            await store.SaveAsync(key, body.GetRawText(), ctx.RequestAborted);
            return Results.NoContent();
        }

        // Start from what the page showed, so fields a client leaves out keep their values.
        var current = await store.GetAsync(key, ctx.RequestAborted) is { } json
            ? EncodingSettings.Deserialize(json)
            : EncodingSettings.FromOptions(options.CurrentValue.Transcoding, pipelines.ActiveHardware, transcodes.TempRoot);
        var dto = EncodingSettings.Overlay(current, body);
        dto.EncoderAppPath = dto.EncoderAppPathDisplay = null; // display only: the ffmpeg path comes from configuration
        if (EncodingSettings.Validate(dto) is { Count: > 0 } errors)
            return Results.Problem(string.Join("; ", errors), statusCode: 400, title: "Invalid encoding settings");
        await sync.SaveAsync(key, EncodingSettings.Serialize(dto), ctx.RequestAborted);
        log.LogInformation("Encoding settings saved: hardware acceleration {Type}, hardware encoding {Encode}, decoding {Codecs}",
            dto.HardwareAccelerationType, dto.EnableHardwareEncoding, string.Join(',', dto.HardwareDecodingCodecs));
        return Results.NoContent();
    }

    private static async Task<JsonElement?> ReadObjectAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IResult> BrandingCss(INamedConfigurationStore store, CancellationToken ct)
    {
        var css = "";
        if (await store.GetAsync("branding", ct) is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("CustomCss", out var v) && v.ValueKind == JsonValueKind.String) css = v.GetString()!;
        }
        return Results.Text(css, "text/css");
    }

    private static async Task<IResult> Status(IOptions<ServerOptions> o, RedisCache cache, ItemRepository items, ScanCoordinator scanner,
        PipelineSelector pipelines)
    {
        var (hits, misses) = cache.Stats;
        return Results.Json(new ServerStatus(o.Value.Version, hits, misses, cache.Generation, cache.IsConnected,
            await items.CountsAsync(), scanner.Status, pipelines.Status.ToList()), AppJson.Default.ServerStatus);
    }
}
