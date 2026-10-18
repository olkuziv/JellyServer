using Npgsql;

namespace JellyServer.DbTool;

/// <summary>Shows where module schemas live: per database, which modules, how big, and whether a server is connected.</summary>
public static class StatusCommand
{
    public static async Task RunAsync(Cli cli, ConnectionResolver resolver, ConnectionPool pool, TextWriter output, CancellationToken ct)
    {
        var databases = new List<DbConnection>();
        foreach (var reference in cli.Values("db")) databases.Add(resolver.Resolve(reference, "db"));

        if (cli.Flag("env"))
        {
            output.WriteLine("Server configuration in the environment:");
            foreach (var spec in ModuleCatalog.Specs)
            {
                var c = resolver.FromServerEnvironment(spec.Module);
                output.WriteLine($"  {spec.Module,-14} {(c is null ? "(not set)" : c.Where)}");
                if (c is not null && !databases.Any(d => Same(d, c))) databases.Add(c);
            }
            output.WriteLine();
        }
        if (databases.Count == 0) throw new ToolException("status needs --db REF (repeatable) and/or --env.");

        foreach (var db in databases)
        {
            var ds = await pool.OpenAsync(db, null, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var version = (await Sql.ScalarAsync<string>(conn, "show server_version", ct)).Split(' ')[0];
            var size = await Sql.ScalarAsync<long>(conn, "select pg_database_size(current_database())", ct);
            var sessions = await Mover.ServerSessionsAsync(conn, ct);
            output.WriteLine($"{db.Where}   PostgreSQL {version}, {Sql.Size(size)}" + (sessions > 0 ? $"   ** {sessions} JellyServer connection(s) open **" : ""));

            foreach (var spec in ModuleCatalog.Specs)
            {
                if (!await Sql.ScalarAsync<bool>(conn, "select exists(select 1 from information_schema.schemata where schema_name = @s)", ct, ("s", spec.Schema)))
                    continue;
                var tables = await Sql.StringsAsync(conn,
                    "select table_name from information_schema.tables where table_schema = @s and table_type = 'BASE TABLE'", ct, ("s", spec.Schema));
                long bytes = 0;
                foreach (var t in tables)
                    bytes += await Sql.ScalarAsync<long>(conn, "select pg_total_relation_size(format('%I.%I', @s, @t)::regclass)", ct, ("s", spec.Schema), ("t", t));
                var known = Kernel.Data.Migrator.ScriptNames(spec).Count;
                string migrations;
                try
                {
                    var applied = await Sql.ScalarAsync<long>(conn, $"select count(*) from {Sql.Q(spec.Schema)}.schema_migrations", ct);
                    migrations = applied == known ? $"{applied}/{known} migrations" : $"{applied}/{known} migrations (differs from this tool)";
                }
                catch (PostgresException) { migrations = "no schema_migrations"; }
                output.WriteLine($"  {spec.Module,-14} schema \"{spec.Schema}\"  {tables.Count} tables  {Sql.Size(bytes),10}  {migrations}");
            }
            output.WriteLine();
        }
    }

    private static bool Same(DbConnection a, DbConnection b) =>
        string.Equals(a.Builder.Host, b.Builder.Host, StringComparison.OrdinalIgnoreCase) && a.Builder.Port == b.Builder.Port &&
        string.Equals(a.Builder.Database, b.Builder.Database, StringComparison.OrdinalIgnoreCase);
}
