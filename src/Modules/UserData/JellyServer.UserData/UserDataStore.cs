using Dapper;
using JellyServer.Kernel.Data;
using JellyServer.Kernel.Events;
using JellyServer.UserData.Contracts;

namespace JellyServer.UserData;

public sealed class UserDataStore([FromKeyedServices(UserDataModule.ModuleName)] Database db, ProgressBuffer buffer, IEventBus events) : IUserDataStore
{
    private Task Changed(Guid userId, IReadOnlyList<Guid> itemIds) => events.PublishAsync(new UserDataChanged(userId, itemIds));

    private const string Columns = "item_id, position_ticks, play_count, played, is_favorite, last_played_at";

    private static ItemUserData ToUserData((Guid ItemId, long Pos, int Count, bool Played, bool Fav, DateTime? Last) r) => new()
    {
        PositionTicks = r.Pos, PlayCount = r.Count, Played = r.Played, IsFavorite = r.Fav,
        LastPlayedAt = r.Last is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
    };

    public async Task<Dictionary<Guid, ItemUserData>> GetManyAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid ItemId, long Pos, int Count, bool Played, bool Fav, DateTime? Last)>(new CommandDefinition(
            $"SELECT {Columns} FROM user_data WHERE user_id = @userId AND item_id = ANY(@ids)",
            new { userId, ids = itemIds.ToArray() }, cancellationToken: ct));
        var result = rows.ToDictionary(r => r.ItemId, ToUserData);
        if (buffer.Available)
            foreach (var (item, p) in await buffer.PendingAsync(userId))
                if (itemIds.Contains(item)) Overlay(result, item, p);
        return result;
    }

    /// <summary>Applies a buffered position on top of the database row (or creates the row the flush will insert).</summary>
    private static void Overlay(Dictionary<Guid, ItemUserData> data, Guid item, (long Position, DateTime At) p)
    {
        if (!data.TryGetValue(item, out var d)) data[item] = d = new ItemUserData();
        d.PositionTicks = p.Position;
        d.LastPlayedAt = p.At;
    }

    public async Task<Dictionary<Guid, ItemUserData>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid ItemId, long Pos, int Count, bool Played, bool Fav, DateTime? Last)>(new CommandDefinition(
            $"SELECT {Columns} FROM user_data WHERE user_id = @userId", new { userId }, cancellationToken: ct));
        var result = rows.ToDictionary(r => r.ItemId, ToUserData);
        if (buffer.Available)
            foreach (var (item, p) in await buffer.PendingAsync(userId)) Overlay(result, item, p);
        return result;
    }

    private async Task<List<Guid>> IdsAsync(string where, Guid userId, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            $"SELECT item_id FROM user_data WHERE user_id = @userId AND {where}", new { userId }, cancellationToken: ct))).ToList();
    }

    public Task<List<Guid>> FavoriteIdsAsync(Guid userId, CancellationToken ct = default) => IdsAsync("is_favorite", userId, ct);

    public Task<List<Guid>> PlayedIdsAsync(Guid userId, CancellationToken ct = default) => IdsAsync("played", userId, ct);

    public async Task<List<Guid>> ResumableIdsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<(Guid ItemId, DateTime? Last, bool Played, long Pos)>(new CommandDefinition("""
            SELECT item_id, last_played_at, played, position_ticks FROM user_data
             WHERE user_id = @userId AND ((position_ticks > 0 AND NOT played) OR @withPending)
             ORDER BY last_played_at DESC NULLS LAST
            """, new { userId, withPending = buffer.Available }, cancellationToken: ct))).ToList();
        if (!buffer.Available) return rows.Select(r => r.ItemId).ToList();

        // Merge positions that are not in the database yet: a pending position > 0 makes an unplayed item resumable.
        var state = rows.ToDictionary(r => r.ItemId, r => (Last: r.Last, r.Played, Pos: r.Pos));
        foreach (var (item, p) in await buffer.PendingAsync(userId))
            state[item] = (p.At, state.TryGetValue(item, out var old) && old.Played, p.Position);
        return state.Where(kv => kv.Value.Pos > 0 && !kv.Value.Played)
            .OrderByDescending(kv => kv.Value.Last ?? DateTime.MinValue).Select(kv => kv.Key).ToList();
    }

    public async Task<List<(Guid ItemId, DateTime? LastPlayedAt)>> PlayedWithTimeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid ItemId, DateTime? Last)>(new CommandDefinition(
            "SELECT item_id, last_played_at FROM user_data WHERE user_id = @userId AND played", new { userId }, cancellationToken: ct));
        return rows.Select(r => (r.ItemId, r.Last is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null)).ToList();
    }

    public async Task<HashSet<Guid>> PlayedAmongAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return [];
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT item_id FROM user_data WHERE user_id = @userId AND played AND item_id = ANY(@ids)",
            new { userId, ids = itemIds.ToArray() }, cancellationToken: ct))).ToHashSet();
    }

    public async Task SaveProgressAsync(Guid userId, Guid itemId, long position, bool durable = false)
    {
        if (!durable && buffer.Available)
        {
            try
            {
                await buffer.EnqueueAsync(userId, itemId, position);
                return;
            }
            catch (StackExchange.Redis.RedisException)
            {
                // Redis hiccup: fall through to a direct write rather than lose the position.
            }
        }
        if (durable) await buffer.DiscardAsync(userId, [itemId]);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO user_data (user_id, item_id, position_ticks, last_played_at) VALUES (@userId, @itemId, @position, now())
            ON CONFLICT (user_id, item_id) DO UPDATE SET position_ticks = EXCLUDED.position_ticks, last_played_at = now()
            """, new { userId, itemId, position });
        if (durable) await Changed(userId, [itemId]);
    }

    public async Task MarkPlayedAsync(Guid userId, Guid itemId, bool played)
    {
        await buffer.DiscardAsync(userId, [itemId]);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO user_data (user_id, item_id, played, play_count, last_played_at)
            VALUES (@userId, @itemId, @played, CASE WHEN @played THEN 1 ELSE 0 END, CASE WHEN @played THEN now() END)
            ON CONFLICT (user_id, item_id) DO UPDATE SET
                played = EXCLUDED.played,
                position_ticks = 0,
                play_count = CASE WHEN EXCLUDED.played THEN user_data.play_count + 1 ELSE user_data.play_count END,
                last_played_at = CASE WHEN EXCLUDED.played THEN now() ELSE user_data.last_played_at END
            """, new { userId, itemId, played });
        await Changed(userId, [itemId]);
    }

    public async Task MarkManyPlayedAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, bool played)
    {
        if (itemIds.Count == 0) return;
        await buffer.DiscardAsync(userId, itemIds);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO user_data (user_id, item_id, played, play_count, last_played_at)
            SELECT @userId, id, @played, CASE WHEN @played THEN 1 ELSE 0 END, CASE WHEN @played THEN now() END
              FROM unnest(@ids) AS id
            ON CONFLICT (user_id, item_id) DO UPDATE SET played = EXCLUDED.played, position_ticks = 0,
                last_played_at = coalesce(EXCLUDED.last_played_at, user_data.last_played_at)
            """, new { userId, played, ids = itemIds.ToArray() });
        await Changed(userId, [.. itemIds]);
    }

    public async Task SetFavoriteAsync(Guid userId, Guid itemId, bool favorite)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO user_data (user_id, item_id, is_favorite) VALUES (@userId, @itemId, @favorite)
            ON CONFLICT (user_id, item_id) DO UPDATE SET is_favorite = EXCLUDED.is_favorite
            """, new { userId, itemId, favorite });
        await Changed(userId, [itemId]);
    }

    public async Task UpdateAsync(Guid userId, Guid itemId, long? position, bool? played, bool? favorite, int? playCount)
    {
        if (position is not null || played is not null) await buffer.DiscardAsync(userId, [itemId]);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO user_data (user_id, item_id, position_ticks, played, is_favorite, play_count)
            VALUES (@userId, @itemId, coalesce(@position::bigint, 0), coalesce(@played::bool, false), coalesce(@favorite::bool, false), coalesce(@playCount::int, 0))
            ON CONFLICT (user_id, item_id) DO UPDATE SET
                position_ticks = coalesce(@position::bigint, user_data.position_ticks),
                played = coalesce(@played::bool, user_data.played),
                is_favorite = coalesce(@favorite::bool, user_data.is_favorite),
                play_count = coalesce(@playCount::int, user_data.play_count)
            """, new { userId, itemId, position, played, favorite, playCount });
        await Changed(userId, [itemId]);
    }

    public async Task<string?> GetDisplayPreferencesAsync(Guid userId, string prefId, string client)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT data::text FROM display_preferences WHERE user_id = @userId AND pref_id = @prefId AND client = @client",
            new { userId, prefId, client });
    }

    public async Task SaveDisplayPreferencesAsync(Guid userId, string prefId, string client, string json)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO display_preferences (user_id, pref_id, client, data) VALUES (@userId, @prefId, @client, @json::jsonb)
            ON CONFLICT (user_id, pref_id, client) DO UPDATE SET data = EXCLUDED.data
            """, new { userId, prefId, client, json });
    }
}
