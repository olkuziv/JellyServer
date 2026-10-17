using System.Globalization;
using Dapper;
using JellyServer.Kernel.Data;
using StackExchange.Redis;

namespace JellyServer.UserData;

public sealed record UserDataOptions(TimeSpan ProgressFlushInterval)
{
    /// <summary>A zero interval disables write-behind: every progress report is written straight to PostgreSQL.</summary>
    public bool WriteBehind => ProgressFlushInterval > TimeSpan.Zero;
}

/// <summary>
/// Write-behind for playback progress. Clients report a position every ~10 s, which would be one PostgreSQL
/// upsert per client per report. Positions go to a Redis hash per user instead; a flusher turns everything that
/// accumulated into ONE statement every few seconds. Readers overlay the pending positions, so the UI never
/// sees stale resume points. Crash safe: hashes are renamed (atomically) before they are flushed, leftovers are
/// replayed, and the upsert only moves <c>last_played_at</c> forward, so a replay never overwrites newer state.
/// </summary>
public sealed class ProgressBuffer(IConnectionMultiplexer redis, [FromKeyedServices(UserDataModule.ModuleName)] Database db, UserDataOptions options,
    ILogger<ProgressBuffer> log) : BackgroundService
{
    private const string Prefix = "js:ud:";
    private const string Dirty = Prefix + "dirty";
    private const int FlushPageUsers = 500;

    private static string Pending(Guid user) => $"{Prefix}p:{user:N}";
    private static string Flushing(Guid user) => $"{Prefix}f:{user:N}";

    private IDatabase Redis => redis.GetDatabase();

    /// <summary>True when progress can be buffered (write-behind on and Redis reachable).</summary>
    public bool Available => options.WriteBehind && redis.IsConnected;

    public async Task EnqueueAsync(Guid userId, Guid itemId, long position)
    {
        var value = $"{position}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var batch = Redis.CreateBatch();
        var set = batch.HashSetAsync(Pending(userId), itemId.ToString("N"), value);
        var dirty = batch.SetAddAsync(Dirty, userId.ToString("N"));
        batch.Execute();
        await Task.WhenAll(set, dirty);
    }

    /// <summary>Forgets buffered positions of items whose state is about to be written directly.</summary>
    public async Task DiscardAsync(Guid userId, IEnumerable<Guid> itemIds)
    {
        if (!redis.IsConnected) return;
        var fields = itemIds.Select(i => (RedisValue)i.ToString("N")).ToArray();
        if (fields.Length == 0) return;
        try
        {
            await Redis.HashDeleteAsync(Pending(userId), fields);
            await Redis.HashDeleteAsync(Flushing(userId), fields);
        }
        catch (RedisException ex)
        {
            log.LogWarning(ex, "Could not discard buffered progress");
        }
    }

    /// <summary>Drops everything buffered for a user (the user was deleted).</summary>
    public async Task ForgetUserAsync(Guid userId)
    {
        if (!redis.IsConnected) return;
        await Redis.KeyDeleteAsync([Pending(userId), Flushing(userId)]);
        await Redis.SetRemoveAsync(Dirty, userId.ToString("N"));
    }

    /// <summary>Positions not yet in PostgreSQL, newest wins (pending over a flush in flight).</summary>
    public async Task<Dictionary<Guid, (long Position, DateTime At)>> PendingAsync(Guid userId)
    {
        var result = new Dictionary<Guid, (long, DateTime)>();
        if (!options.WriteBehind || !redis.IsConnected) return result;
        try
        {
            var flushing = Redis.HashGetAllAsync(Flushing(userId));
            var pending = Redis.HashGetAllAsync(Pending(userId));
            foreach (var entries in new[] { await flushing, await pending }) // later (pending) overrides
                foreach (var e in entries)
                    if (Parse(e) is { } p && Guid.TryParseExact(e.Name.ToString(), "N", out var item)) result[item] = (p.Position, p.At);
        }
        catch (RedisException ex)
        {
            log.LogWarning(ex, "Could not read buffered progress");
        }
        return result;
    }

    private static (long Position, DateTime At)? Parse(HashEntry e)
    {
        var parts = e.Value.ToString().Split('|');
        return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var pos) && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var ms)
            ? (pos, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime)
            : null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.WriteBehind) return;
        using var timer = new PeriodicTimer(options.ProgressFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (options.WriteBehind) await FlushAsync(CancellationToken.None); // do not lose the last seconds on shutdown
    }

    /// <summary>Writes every buffered position with one statement per page of users; returns the number of rows.</summary>
    public async Task<int> FlushAsync(CancellationToken ct)
    {
        if (!redis.IsConnected) return 0;
        var total = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var users = (await Redis.SetPopAsync(Dirty, FlushPageUsers)).Select(v => Guid.ParseExact(v.ToString(), "N")).ToList();
                if (users.Count == 0) break;
                try
                {
                    total += await FlushUsersAsync(users, ct);
                }
                catch
                {
                    // Put them back: the data is still in Redis.
                    await Redis.SetAddAsync(Dirty, users.Select(x => (RedisValue)x.ToString("N")).ToArray());
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is RedisException or Npgsql.NpgsqlException)
        {
            log.LogWarning(ex, "Progress flush failed, will retry");
        }
        return total;
    }

    private async Task<int> FlushUsersAsync(List<Guid> users, CancellationToken ct)
    {
        var userIds = new List<Guid>();
        var itemIds = new List<Guid>();
        var positions = new List<long>();
        var times = new List<DateTime>();
        var flushKeys = new List<RedisKey>();

        foreach (var user in users)
        {
            // A leftover from a crashed flush is replayed first; RENAMENX must never overwrite it.
            if (!await Redis.KeyExistsAsync(Flushing(user)) && await Redis.KeyExistsAsync(Pending(user)))
                await Redis.KeyRenameAsync(Pending(user), Flushing(user), When.NotExists);
            var entries = await Redis.HashGetAllAsync(Flushing(user));
            flushKeys.Add(Flushing(user));
            foreach (var e in entries)
                if (Parse(e) is { } p && Guid.TryParseExact(e.Name.ToString(), "N", out var item))
                {
                    userIds.Add(user);
                    itemIds.Add(item);
                    positions.Add(p.Position);
                    times.Add(p.At);
                }
            // Positions written while the previous hash was still being flushed.
            if (await Redis.KeyExistsAsync(Pending(user))) await Redis.SetAddAsync(Dirty, user.ToString("N"));
        }

        if (userIds.Count > 0)
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO user_data (user_id, item_id, position_ticks, last_played_at)
                SELECT * FROM unnest(@users, @items, @positions, @times)
                ON CONFLICT (user_id, item_id) DO UPDATE
                   SET position_ticks = EXCLUDED.position_ticks, last_played_at = EXCLUDED.last_played_at
                 WHERE user_data.last_played_at IS NULL OR user_data.last_played_at <= EXCLUDED.last_played_at
                """, new { users = userIds.ToArray(), items = itemIds.ToArray(), positions = positions.ToArray(), times = times.ToArray() }, cancellationToken: ct));
            log.LogDebug("Flushed {Rows} progress rows of {Users} users", userIds.Count, users.Count);
        }
        await Redis.KeyDeleteAsync(flushKeys.ToArray());
        return userIds.Count;
    }
}
