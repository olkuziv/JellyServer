using System.Text.Json;
using Dapper;
using JellyServer.Kernel.Data;
using JellyServer.Plugins.Contracts;

namespace JellyServer.Plugins;

/// <summary>The plugin_kv table: every plugin's documents, always addressed together with the plugin id.</summary>
public sealed class PluginStore([FromKeyedServices(PluginsModule.ModuleName)] Database db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Row(string Key, string Value);

    public async Task<T?> GetAsync<T>(string plugin, string collection, string key, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var json = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT value::text FROM plugin_kv WHERE plugin = @plugin AND collection = @collection AND key = @key",
            new { plugin, collection, key }, cancellationToken: ct));
        return json is null ? default : JsonSerializer.Deserialize<T>(json, Json);
    }

    public async Task SetAsync<T>(string plugin, string collection, string key, T value, CancellationToken ct) =>
        await SetManyAsync(plugin, collection, [KeyValuePair.Create(key, value)], ct);

    public async Task SetManyAsync<T>(string plugin, string collection, IEnumerable<KeyValuePair<string, T>> items, CancellationToken ct)
    {
        var rows = items.Select(i => new { plugin, collection, key = i.Key, json = JsonSerializer.Serialize(i.Value, Json) }).ToList();
        if (rows.Count == 0) return;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO plugin_kv (plugin, collection, key, value) VALUES (@plugin, @collection, @key, @json::jsonb)
            ON CONFLICT (plugin, collection, key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """, rows, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(string plugin, string collection, string key, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM plugin_kv WHERE plugin = @plugin AND collection = @collection AND key = @key",
            new { plugin, collection, key }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyDictionary<string, T>> ListAsync<T>(string plugin, string collection, string? keyPrefix, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT key AS Key, value::text AS Value FROM plugin_kv
            WHERE plugin = @plugin AND collection = @collection AND (@like::text IS NULL OR key LIKE @like ESCAPE '\')
            ORDER BY key COLLATE "C"
            """, new { plugin, collection, like = keyPrefix is null ? null : EscapeLike(keyPrefix) + "%" }, cancellationToken: ct));
        var result = new Dictionary<string, T>();
        foreach (var row in rows) result[row.Key] = JsonSerializer.Deserialize<T>(row.Value, Json)!;
        return result;
    }

    public async Task<int> DeleteCollectionAsync(string plugin, string collection, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM plugin_kv WHERE plugin = @plugin AND collection = @collection", new { plugin, collection }, cancellationToken: ct));
    }

    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <summary>The store as one plugin sees it: the plugin id is fixed, so plugins cannot reach each other's documents.</summary>
internal sealed class PluginStorage(string pluginId, PluginStore store) : IPluginStorage
{
    public Task<T?> GetAsync<T>(string collection, string key, CancellationToken ct = default) => store.GetAsync<T>(pluginId, collection, key, ct);
    public Task SetAsync<T>(string collection, string key, T value, CancellationToken ct = default) => store.SetAsync(pluginId, collection, key, value, ct);
    public Task SetManyAsync<T>(string collection, IEnumerable<KeyValuePair<string, T>> items, CancellationToken ct = default) => store.SetManyAsync(pluginId, collection, items, ct);
    public Task<bool> DeleteAsync(string collection, string key, CancellationToken ct = default) => store.DeleteAsync(pluginId, collection, key, ct);
    public Task<IReadOnlyDictionary<string, T>> ListAsync<T>(string collection, string? keyPrefix = null, CancellationToken ct = default) => store.ListAsync<T>(pluginId, collection, keyPrefix, ct);
    public Task<int> DeleteCollectionAsync(string collection, CancellationToken ct = default) => store.DeleteCollectionAsync(pluginId, collection, ct);
}
