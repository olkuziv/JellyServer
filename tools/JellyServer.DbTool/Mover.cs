using System.Data;
using System.Diagnostics;
using JellyServer.Kernel.Data;
using Npgsql;

namespace JellyServer.DbTool;

public sealed record MoveRequest(ModuleDatabaseSpec Spec, DbConnection From, DbConnection To);

public sealed record MoveOptions(bool DryRun, bool Replace, bool DropSource, bool IgnoreRunning);

/// <summary>
/// Moves one module's schema from one PostgreSQL database to another. The target gets the module's own migrations
/// (so it works across PostgreSQL versions and never depends on pg_dump), then every table is streamed with binary COPY,
/// sequences are realigned, and row counts are compared inside the target transaction before it commits.
/// The source is only read; it loses the schema only with <see cref="MoveOptions.DropSource"/> after a verified copy.
/// </summary>
public sealed class Mover(ConnectionPool pool, MoveOptions options, TextWriter output)
{
    public async Task MoveAsync(MoveRequest request, CancellationToken ct)
    {
        var spec = request.Spec;
        var schema = spec.Schema;
        if (SamePlace(request.From, request.To))
            throw new ToolException($"[{spec.Module}] source and target are the same database ({request.From.Where}); nothing to move.");

        var srcDs = await pool.OpenAsync(request.From, schema, ct);
        var dstDs = await pool.OpenAsync(request.To, schema, ct);
        var watch = Stopwatch.StartNew();
        output.WriteLine($"== {spec.Module}  ({request.From.Where} -> {request.To.Where}, schema \"{schema}\")");

        await using var src = await srcDs.OpenConnectionAsync(ct);
        await using var srcTx = await src.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        // ---- source preflight -------------------------------------------------------------------------------------------
        if (!await Sql.ScalarAsync<bool>(src, "select exists(select 1 from information_schema.schemata where schema_name = @s)", ct, ("s", schema)))
            throw new ToolException($"[{spec.Module}] the source has no schema \"{schema}\". Is the module stored in another database?");
        var tables = await Sql.StringsAsync(src,
            "select table_name from information_schema.tables where table_schema = @s and table_type = 'BASE TABLE' order by 1", ct, ("s", schema));
        await RequireMatchingMigrationsAsync(src, spec, ct);
        if (!options.IgnoreRunning && await ServerSessionsAsync(src, ct) is var running and > 0)
            throw new ToolException($"[{spec.Module}] {running} JellyServer connection(s) are open on the source database. Stop the server first " +
                                    "(a copy taken while it writes can miss changes), or pass --ignore-running to copy a consistent snapshot anyway.");

        var counts = new Dictionary<string, long>();
        var sizes = new Dictionary<string, long>();
        foreach (var t in tables)
        {
            counts[t] = await Sql.ScalarAsync<long>(src, $"select count(*) from {Sql.Q(schema)}.{Sql.Q(t)}", ct);
            sizes[t] = await Sql.ScalarAsync<long>(src, "select pg_total_relation_size(format('%I.%I', @s, @t)::regclass)", ct, ("s", schema), ("t", t));
        }

        // ---- target preflight -------------------------------------------------------------------------------------------
        await using var probe = await dstDs.OpenConnectionAsync(ct);
        var targetExists = await Sql.ScalarAsync<bool>(probe, "select exists(select 1 from information_schema.schemata where schema_name = @s)", ct, ("s", schema));
        if (targetExists && !options.Replace)
        {
            foreach (var t in await Sql.StringsAsync(probe,
                         "select table_name from information_schema.tables where table_schema = @s and table_type = 'BASE TABLE' and table_name <> 'schema_migrations' order by 1", ct, ("s", schema)))
                if (await Sql.ScalarAsync<bool>(probe, $"select exists(select 1 from {Sql.Q(schema)}.{Sql.Q(t)})", ct))
                    throw new ToolException($"[{spec.Module}] the target already has data in \"{schema}\".\"{t}\". " +
                                            "Pass --replace to drop that schema on the target first (the source is untouched).");
        }

        foreach (var t in tables)
            output.WriteLine($"   {t,-28} {counts[t],12:N0} rows  {Sql.Size(sizes[t]),10}");
        output.WriteLine($"   total {counts.Values.Sum():N0} rows, {Sql.Size(sizes.Values.Sum())}");
        if (options.DryRun)
        {
            output.WriteLine("   dry run: nothing written.");
            return;
        }

        // ---- copy -------------------------------------------------------------------------------------------------------
        var created = !targetExists || options.Replace;
        if (targetExists && options.Replace)
        {
            await Sql.ExecAsync(probe, $"drop schema {Sql.Q(schema)} cascade", ct);
            output.WriteLine($"   dropped existing target schema \"{schema}\"");
        }
        await Migrator.RunAsync(dstDs, spec, ct);

        try
        {
            await using var dst = await dstDs.OpenConnectionAsync(ct);
            await using var dstTx = await dst.BeginTransactionAsync(ct);

            var targetTables = await Sql.StringsAsync(dst,
                "select table_name from information_schema.tables where table_schema = @s and table_type = 'BASE TABLE' order by 1", ct, ("s", schema));
            if (!targetTables.SequenceEqual(tables, StringComparer.Ordinal))
                throw new ToolException($"[{spec.Module}] source and target tables differ (source: {string.Join(", ", tables)}; target: {string.Join(", ", targetTables)}).");

            // Seeded rows from migrations must not collide with the copied ones.
            if (tables.Count > 0)
                await Sql.ExecAsync(dst, "truncate " + string.Join(", ", tables.Select(t => $"{Sql.Q(schema)}.{Sql.Q(t)}")), ct);
            await Sql.ExecAsync(dst, "set constraints all deferred", ct);

            foreach (var t in await OrderByDependenciesAsync(src, schema, tables, ct))
            {
                if (counts[t] == 0) continue;
                var columns = string.Join(", ", (await Sql.StringsAsync(src,
                        "select column_name from information_schema.columns where table_schema = @s and table_name = @t and is_generated = 'NEVER' order by ordinal_position",
                        ct, ("s", schema), ("t", t)))
                    .Select(Sql.Q));
                var from = $"{Sql.Q(schema)}.{Sql.Q(t)}";
                await using (var reader = await src.BeginRawBinaryCopyAsync($"copy (select {columns} from {from}) to stdout (format binary)", ct))
                await using (var writer = await dst.BeginRawBinaryCopyAsync($"copy {from} ({columns}) from stdin (format binary)", ct))
                    await reader.CopyToAsync(writer, 1 << 16, ct);
            }

            await RealignSequencesAsync(dst, schema, tables, ct);

            foreach (var t in tables)
            {
                var copied = await Sql.ScalarAsync<long>(dst, $"select count(*) from {Sql.Q(schema)}.{Sql.Q(t)}", ct);
                if (copied != counts[t])
                    throw new ToolException($"[{spec.Module}] row count differs in \"{t}\": source {counts[t]:N0}, target {copied:N0}. Nothing was committed.");
            }
            await dstTx.CommitAsync(ct);
        }
        catch
        {
            if (created)
            {
                try { await Sql.ExecAsync(probe, $"drop schema if exists {Sql.Q(schema)} cascade", CancellationToken.None); } catch { /* best effort */ }
            }
            throw;
        }

        await srcTx.RollbackAsync(ct);
        output.WriteLine($"   copied and verified in {watch.Elapsed.TotalSeconds:N1} s");

        if (options.DropSource)
        {
            await Sql.ExecAsync(src, $"drop schema {Sql.Q(schema)} cascade", ct);
            output.WriteLine($"   dropped \"{schema}\" on the source");
        }
    }

    private static bool SamePlace(DbConnection a, DbConnection b) =>
        string.Equals(a.Builder.Host, b.Builder.Host, StringComparison.OrdinalIgnoreCase) && a.Builder.Port == b.Builder.Port &&
        string.Equals(a.Builder.Database, b.Builder.Database, StringComparison.OrdinalIgnoreCase);

    private static async Task RequireMatchingMigrationsAsync(NpgsqlConnection src, ModuleDatabaseSpec spec, CancellationToken ct)
    {
        var known = Migrator.ScriptNames(spec);
        List<string> applied;
        try
        {
            applied = await Sql.StringsAsync(src, $"select name from {Sql.Q(spec.Schema)}.schema_migrations order by 1", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            throw new ToolException($"[{spec.Module}] the source schema has no schema_migrations table; it was not created by JellyServer.");
        }

        var missing = known.Except(applied, StringComparer.Ordinal).ToList();
        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new ToolException($"[{spec.Module}] the source is behind this tool's version (missing migration {string.Join(", ", missing)}). " +
                                    "Start a server of this version once so it migrates, then run the tool again.");
        if (unknown.Count > 0)
            throw new ToolException($"[{spec.Module}] the source is newer than this tool (unknown migration {string.Join(", ", unknown)}). Use a matching dbtool.");
    }

    internal static Task<long> ServerSessionsAsync(NpgsqlConnection conn, CancellationToken ct) => Sql.ScalarAsync<long>(conn,
        "select count(*) from pg_stat_activity where datname = current_database() and application_name like 'jellyserver-%' " +
        "and application_name <> 'jellyserver-dbtool' and pid <> pg_backend_pid()", ct);

    /// <summary>Parents before children, so foreign keys hold while copying (cycles and self references fall back to alphabetical order).</summary>
    private static async Task<List<string>> OrderByDependenciesAsync(NpgsqlConnection conn, string schema, List<string> tables, CancellationToken ct)
    {
        var deps = tables.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("""
            select child.relname, parent.relname
            from pg_constraint c
            join pg_class child on child.oid = c.conrelid
            join pg_namespace cn on cn.oid = child.relnamespace
            join pg_class parent on parent.oid = c.confrelid
            join pg_namespace pn on pn.oid = parent.relnamespace
            where c.contype = 'f' and cn.nspname = @s and pn.nspname = @s and child.oid <> parent.oid
            """, conn))
        {
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (deps.TryGetValue(r.GetString(0), out var set)) set.Add(r.GetString(1));
        }

        var ordered = new List<string>();
        var remaining = tables.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.Where(t => deps[t].All(ordered.Contains)).ToList();
            if (next.Count == 0) next = [remaining[0]];
            foreach (var t in next) { ordered.Add(t); remaining.Remove(t); }
        }
        return ordered;
    }

    /// <summary>Serial/identity counters continue after the copied rows instead of restarting at 1.</summary>
    private static async Task RealignSequencesAsync(NpgsqlConnection dst, string schema, List<string> tables, CancellationToken ct)
    {
        foreach (var t in tables)
        {
            var found = new List<(string Column, string Sequence)>();
            await using (var cmd = new NpgsqlCommand("""
                select a.attname, pg_get_serial_sequence(format('%I.%I', @s, @t), a.attname)
                from pg_attribute a
                where a.attrelid = format('%I.%I', @s, @t)::regclass and a.attnum > 0 and not a.attisdropped
                """, dst))
            {
                cmd.Parameters.AddWithValue("s", schema);
                cmd.Parameters.AddWithValue("t", t);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    if (!r.IsDBNull(1)) found.Add((r.GetString(0), r.GetString(1)));
            }
            foreach (var (column, sequence) in found)
                await Sql.ExecAsync(dst,
                    $"select setval('{sequence.Replace("'", "''")}'::regclass, coalesce(max({Sql.Q(column)}), 1), max({Sql.Q(column)}) is not null) from {Sql.Q(schema)}.{Sql.Q(t)}", ct);
        }
    }
}

/// <summary>Small ADO.NET helpers (the tool has no ORM).</summary>
internal static class Sql
{
    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KB",
        _ => $"{bytes} B",
    };

    public static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(conn, sql, args);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T));
    }

    public static async Task<List<string>> StringsAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        var list = new List<string>();
        await using var cmd = Command(conn, sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, string sql, (string Name, object Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
