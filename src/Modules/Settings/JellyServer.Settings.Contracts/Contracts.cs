using System.Text.Json;
using System.Text.Json.Nodes;

namespace JellyServer.Settings.Contracts;

/// <summary>Named configurations (Jellyfin's <c>/System/Configuration/{key}</c>) stored as JSON.</summary>
public interface INamedConfigurationStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SaveAsync(string key, string json, CancellationToken ct = default);

    /// <summary>Latest change of any key: replicas poll it to notice saves made elsewhere.</summary>
    Task<DateTime?> LastChangeAsync(CancellationToken ct = default);
}

/// <summary>The main server configuration document (Jellyfin's ServerConfiguration).</summary>
public interface IServerConfiguration
{
    /// <summary>The stored document (a clone: callers may edit it freely).</summary>
    Task<JsonObject> GetAsync(CancellationToken ct = default);

    Task<bool> IsStartupCompletedAsync(CancellationToken ct = default);

    /// <summary>Applies an edit to the document and saves it.</summary>
    Task UpdateAsync(Action<JsonObject> edit, CancellationToken ct = default);

    /// <summary>Replaces the whole document (POST /System/Configuration).</summary>
    Task ReplaceAsync(JsonElement body, CancellationToken ct = default);

    Task CompleteStartupAsync(CancellationToken ct = default);

    Task<string?> ServerNameAsync(CancellationToken ct = default);
}