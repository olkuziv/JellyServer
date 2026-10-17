using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StackExchange.Redis;

namespace JellyServer.Kernel.Caching;

/// <summary>
/// Redis wrapper. Every call degrades gracefully: an outage turns into cache
/// misses, never into failed requests.
/// </summary>
public sealed class RedisCache : IAsyncDisposable
{
    private const string Prefix = "js:";
    private const string GenerationKey = Prefix + "library:gen";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCache> _log;
    private long _generation;
    private long _hits, _misses;

    public RedisCache(IConnectionMultiplexer redis, ILogger<RedisCache> log)
    {
        _redis = redis;
        _log = log;
    }

    private IDatabase Db => _redis.GetDatabase();

    public static async Task<IConnectionMultiplexer> ConnectAsync(string connectionString, ILogger log, CancellationToken ct)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false; // keep retrying in the background
        options.ConnectRetry = 5;
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        for (var attempt = 1; !mux.IsConnected && attempt <= 30; attempt++)
        {
            log.LogWarning("Redis not ready (attempt {Attempt})", attempt);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return mux;
    }

    public (long Hits, long Misses) Stats => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));
    public bool IsConnected => _redis.IsConnected;

    // ---- library generation: bumping it invalidates every cached query ----

    /// <summary>Locally mirrored so the hot path never waits on Redis.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public async Task RefreshGenerationAsync()
    {
        try
        {
            var v = await Db.StringGetAsync(GenerationKey);
            if (v.TryParse(out long n)) Interlocked.Exchange(ref _generation, n);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogDebug("Redis generation refresh failed: {Error}", ex.Message);
        }
    }

    public async Task BumpGenerationAsync()
    {
        try
        {
            Interlocked.Exchange(ref _generation, await Db.StringIncrementAsync(GenerationKey));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogWarning("Redis bump generation failed: {Error}", ex.Message);
            Interlocked.Increment(ref _generation);
        }
    }

    // ---- JSON values ----

    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> type)
    {
        try
        {
            var v = await Db.StringGetAsync(Prefix + key);
            if (v.HasValue)
            {
                Interlocked.Increment(ref _hits);
                return JsonSerializer.Deserialize((byte[])v!, type);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            _log.LogDebug("Redis get {Key} failed: {Error}", key, ex.Message);
        }
        Interlocked.Increment(ref _misses);
        return default;
    }

    public async Task SetAsync<T>(string key, T value, JsonTypeInfo<T> type, TimeSpan ttl)
    {
        try
        {
            await Db.StringSetAsync(Prefix + key, JsonSerializer.SerializeToUtf8Bytes(value, type), ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogDebug("Redis set {Key} failed: {Error}", key, ex.Message);
        }
    }

    /// <summary>Fetches many keys in one round trip; missing entries are null.</summary>
    public async Task<T?[]> GetManyAsync<T>(IReadOnlyList<string> keys, JsonTypeInfo<T> type) where T : class
    {
        var result = new T?[keys.Count];
        if (keys.Count == 0) return result;
        try
        {
            var values = await Db.StringGetAsync(keys.Select(k => (RedisKey)(Prefix + k)).ToArray());
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i].HasValue) continue;
                try { result[i] = JsonSerializer.Deserialize((byte[])values[i]!, type); }
                catch (JsonException) { /* stale shape: treat as miss */ }
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogDebug("Redis mget failed: {Error}", ex.Message);
        }
        var hits = result.Count(r => r is not null);
        Interlocked.Add(ref _hits, hits);
        Interlocked.Add(ref _misses, keys.Count - hits);
        return result;
    }

    public async Task SetManyAsync<T>(IEnumerable<(string Key, T Value)> entries, JsonTypeInfo<T> type, TimeSpan ttl)
    {
        try
        {
            var batch = Db.CreateBatch();
            var tasks = entries
                .Select(e => batch.StringSetAsync(Prefix + e.Key, JsonSerializer.SerializeToUtf8Bytes(e.Value, type), ttl))
                .ToList();
            if (tasks.Count == 0) return;
            batch.Execute();
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogDebug("Redis mset failed: {Error}", ex.Message);
        }
    }

    public async Task DeleteAsync(IEnumerable<string> keys)
    {
        try
        {
            foreach (var chunk in keys.Select(k => (RedisKey)(Prefix + k)).Chunk(1000))
                await Db.KeyDeleteAsync(chunk);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogWarning("Redis delete failed: {Error}", ex.Message);
        }
    }

    public Task DeleteAsync(params string[] keys) => DeleteAsync((IEnumerable<string>)keys);

    // ---- distributed lock: only one replica scans at a time ----

    public async Task<IAsyncDisposable?> TryLockAsync(string name, TimeSpan ttl)
    {
        var key = Prefix + "lock:" + name;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            if (!await Db.LockTakeAsync(key, token, ttl)) return null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _log.LogWarning("Redis lock unavailable, proceeding without it: {Error}", ex.Message);
            return new NoopLock();
        }
        return new RedisLock(Db, key, token, ttl);
    }

    private sealed class NoopLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RedisLock : IAsyncDisposable
    {
        private readonly IDatabase _db;
        private readonly RedisKey _key;
        private readonly RedisValue _token;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _renewal;

        public RedisLock(IDatabase db, RedisKey key, RedisValue token, TimeSpan ttl)
        {
            (_db, _key, _token) = (db, key, token);
            _renewal = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(ttl / 3);
                try
                {
                    while (await timer.WaitForNextTickAsync(_cts.Token))
                        await _db.LockExtendAsync(_key, _token, ttl);
                }
                catch (OperationCanceledException) { }
                catch (RedisException) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _renewal;
            try { await _db.LockReleaseAsync(_key, _token); } catch (RedisException) { }
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await _redis.DisposeAsync();
}
