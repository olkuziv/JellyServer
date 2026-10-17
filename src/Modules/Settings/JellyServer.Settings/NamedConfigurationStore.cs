using Dapper;
using JellyServer.Kernel.Data;
using JellyServer.Settings.Contracts;

namespace JellyServer.Settings;

/// <summary>Named configurations (Jellyfin's <c>/System/Configuration/{key}</c>) stored as JSON in PostgreSQL.</summary>
public sealed class NamedConfigurationStore([FromKeyedServices(SettingsModule.ModuleName)] Database db) : INamedConfigurationStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT data::text FROM server_configuration WHERE key = @key", new { key }, cancellationToken: ct));
    }

    public async Task SaveAsync(string key, string json, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO server_configuration (key, data) VALUES (@key, @json::jsonb)
            ON CONFLICT (key) DO UPDATE SET data = EXCLUDED.data, updated_at = now()
            """, new { key, json }, cancellationToken: ct));
    }

    /// <summary>Latest change of any key: replicas poll it to notice saves made elsewhere.</summary>
    public async Task<DateTime?> LastChangeAsync(CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(updated_at) FROM server_configuration", cancellationToken: ct));
    }
}
