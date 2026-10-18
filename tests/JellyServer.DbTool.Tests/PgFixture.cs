using JellyServer.DbTool;
using JellyServer.Kernel.Data;
using Npgsql;
using Xunit;

namespace JellyServer.DbTool.Tests;

/// <summary>Runs only when JELLY_TEST_PG points at a PostgreSQL server where the tests may create databases (superuser or CREATEDB).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PgServer.Admin))
            Skip = "Set JELLY_TEST_PG (e.g. 'Host=localhost;Port=55432;Username=postgres;Password=test') to run database tests.";
    }
}

public static class PgServer
{
    public static string? Admin => Environment.GetEnvironmentVariable("JELLY_TEST_PG");
}

/// <summary>Two empty databases on the test server, dropped afterwards.</summary>
public sealed class TwoDatabases : IAsyncDisposable
{
    public string Source { get; } = Create();
    public string Target { get; } = Create();

    private static string Create()
    {
        var name = "dbtool_" + Guid.NewGuid().ToString("N")[..12];
        using var conn = new NpgsqlConnection(PgServer.Admin);
        conn.Open();
        using var cmd = new NpgsqlCommand($"create database {name}", conn);
        cmd.ExecuteNonQuery();
        return new NpgsqlConnectionStringBuilder(PgServer.Admin) { Database = name }.ConnectionString;
    }

    /// <summary>Creates the module's schema through the server's own migrations and runs <paramref name="sql"/> inside it.</summary>
    public static async Task SeedAsync(string connection, string module, params string[] sql)
    {
        var spec = ModuleCatalog.Find(module)!;
        await using var ds = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { SearchPath = spec.Schema }.ConnectionString);
        await Migrator.RunAsync(ds, spec, CancellationToken.None);
        foreach (var s in sql) await ExecAsync(ds, s);
    }

    public static async Task<long> CountAsync(string connection, string schema, string table)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"select count(*) from \"{schema}\".\"{table}\"", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<bool> SchemaExistsAsync(string connection, string schema)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select exists(select 1 from information_schema.schemata where schema_name = @s)", conn);
        cmd.Parameters.AddWithValue("s", schema);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task ExecAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(PgServer.Admin);
        await conn.OpenAsync();
        foreach (var cs in new[] { Source, Target })
        {
            await using var cmd = new NpgsqlCommand($"drop database if exists {new NpgsqlConnectionStringBuilder(cs).Database} with (force)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
