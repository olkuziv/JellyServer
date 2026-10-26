using System.Reflection;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JellyServer.Kernel.Data;

/// <summary>
/// What a module needs from PostgreSQL: its schema, a default pool size and where its embedded
/// <c>NNN_name.sql</c> migrations live. The connection comes from <c>ConnectionStrings:&lt;Module&gt;</c>;
/// without it the module shares the <c>ConnectionStrings:Postgres</c> server and only gets its own schema,
/// so splitting a module onto another server is a configuration change.
/// </summary>
public sealed record ModuleDatabaseSpec(string Module, string Schema, int PoolSize, Assembly Assembly, string MigrationsPrefix);

/// <summary>All module databases: opened once, migrated once, resolved by module name.</summary>
public sealed class DatabaseSet : IAsyncDisposable
{
    private const string DefaultConnection = "Host=localhost;Username=jelly;Password=jelly;Database=jelly";

    private readonly Dictionary<string, Database> _byModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleDatabaseSpec> _specs = new(StringComparer.Ordinal);
    private readonly ILogger<DatabaseSet> _log;

    public DatabaseSet(IEnumerable<ModuleDatabaseSpec> specs, IConfiguration configuration, DatabaseSettings settings,
        ILoggerFactory loggers)
    {
        _log = loggers.CreateLogger<DatabaseSet>();
        var shared = configuration.GetConnectionString("Postgres") ?? DefaultConnection;
        // An empty value (an unset variable in a compose file) means "not configured", like a missing one.
        var specList = specs.Select(spec => (Spec: spec,
            Own: configuration.GetConnectionString(spec.Module) is { } s && !string.IsNullOrWhiteSpace(s) ? s : null)).ToList();

        // "Maximum Pool Size" in the shared string is the connection budget of the whole role (managed servers cap
        // connections per role): it is split between the modules using it in proportion to their default pool sizes.
        var sharedBuilder = new NpgsqlConnectionStringBuilder(shared);
        var budget = sharedBuilder.ContainsKey("Maximum Pool Size") ? sharedBuilder.MaxPoolSize : (int?)null;
        var sharedWeight = specList.Where(m => m.Own is null).Sum(m => m.Spec.PoolSize);

        foreach (var (spec, own) in specList)
        {
            var builder = new NpgsqlConnectionStringBuilder(own ?? shared)
            {
                SearchPath = spec.Schema,
                ApplicationName = "jellyserver-" + spec.Module,
            };
            // A dedicated connection string keeps its own size; without one the module gets its default or its share of the budget.
            if (own is null) builder.MaxPoolSize = budget is { } b ? Math.Max(1, b * spec.PoolSize / sharedWeight) : spec.PoolSize;
            else if (!builder.ContainsKey("Maximum Pool Size")) builder.MaxPoolSize = spec.PoolSize;
            _specs[spec.Module] = spec;
            _byModule[spec.Module] = new Database(spec.Module, spec.Schema, new NpgsqlDataSourceBuilder(builder.ConnectionString).Build(),
                settings, loggers.CreateLogger<Database>());
        }
    }

    public Database this[string module] => _byModule.TryGetValue(module, out var db)
        ? db
        : throw new InvalidOperationException($"No database registered for module '{module}'");

    public IReadOnlyCollection<Database> All => _byModule.Values;

    /// <summary>Waits for every server, then applies each module's pending migrations (once, under an advisory lock).</summary>
    public async Task MigrateAllAsync(CancellationToken ct)
    {
        foreach (var db in _byModule.Values) await db.WaitUntilReadyAsync(ct);
        foreach (var (module, db) in _byModule)
        {
            var applied = await Migrator.RunAsync(db.DataSource, _specs[module], ct);
            foreach (var name in applied) _log.LogInformation("[{Module}] applied migration {Name}", module, name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var db in _byModule.Values) await db.DataSource.DisposeAsync();
    }
}

/// <summary>Applies a module's embedded migrations. Public so offline tools (database mover) can prepare a schema the way the server does.</summary>
public static class Migrator
{
    /// <summary>Names of the module's embedded migrations (as stored in <c>schema_migrations</c>), in the order they run.</summary>
    public static IReadOnlyList<string> ScriptNames(ModuleDatabaseSpec spec) => spec.Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(spec.MigrationsPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal)
        .Select(n => n[spec.MigrationsPrefix.Length..])
        .ToList();

    /// <summary>Runs pending migrations on a data source whose <c>search_path</c> is the module's schema.</summary>
    public static async Task<List<string>> RunAsync(NpgsqlDataSource dataSource, ModuleDatabaseSpec spec, CancellationToken ct)
    {
        var applied = new List<string>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{spec.Schema}\"");
        // Replicas starting together must not apply the same script twice.
        await conn.ExecuteAsync("SELECT pg_advisory_lock(hashtext(@key))", new { key = "jellyserver:" + spec.Schema });
        try
        {
            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())
                """);
            var scripts = spec.Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(spec.MigrationsPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var resource in scripts)
            {
                var name = resource[spec.MigrationsPrefix.Length..];
                if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name = @name)", new { name }))
                    continue;

                await using var stream = spec.Assembly.GetManifestResourceStream(resource)!;
                var sql = await new StreamReader(stream).ReadToEndAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations(name) VALUES (@name)", new { name }, tx);
                await tx.CommitAsync(ct);
                applied.Add(name);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@key))", new { key = "jellyserver:" + spec.Schema });
        }
        return applied;
    }
}

public static class DatabaseServiceExtensions
{
    /// <summary>Registers a module's database; inject it with <c>[FromKeyedServices(moduleName)] Database</c>.</summary>
    public static IServiceCollection AddModuleDatabase(this IServiceCollection services, ModuleDatabaseSpec spec)
    {
        services.AddSingleton(spec);
        services.TryAddSingleton<DatabaseSet>();
        services.AddKeyedSingleton<Database>(spec.Module, (sp, _) => sp.GetRequiredService<DatabaseSet>()[spec.Module]);
        return services;
    }
}
