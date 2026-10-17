using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JellyServer.Kernel.Data;

public sealed record DatabaseSettings(int SlowQueryMs);

/// <summary>One module's Npgsql data source plus slow-query logging.</summary>
public sealed class Database
{
    private readonly ILogger<Database> _log;
    private readonly long _slowTicks;

    public string Module { get; }
    public string Schema { get; }
    public NpgsqlDataSource DataSource { get; }

    internal Database(string module, string schema, NpgsqlDataSource dataSource, DatabaseSettings settings, ILogger<Database> log)
    {
        Module = module;
        Schema = schema;
        DataSource = dataSource;
        _log = log;
        _slowTicks = TimeSpan.FromMilliseconds(settings.SlowQueryMs).Ticks;
    }

    /// <summary>Times a database operation and logs it when it exceeds the slow-query threshold.</summary>
    public async Task<T> Timed<T>(string sql, Func<Task<T>> run)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "[{Module}] query failed: {Sql}", Module, Compact(sql));
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (_slowTicks > 0 && elapsed.Ticks >= _slowTicks)
                _log.LogWarning("[{Module}] slow query {Ms} ms: {Sql}", Module, (long)elapsed.TotalMilliseconds, Compact(sql));
        }
    }

    public async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await DataSource.OpenConnectionAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < 30 && ex is NpgsqlException or System.Net.Sockets.SocketException)
            {
                _log.LogWarning("[{Module}] database not ready (attempt {Attempt}): {Error}", Module, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static string Compact(string sql)
    {
        var s = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}
