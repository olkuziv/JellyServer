using JellyServer.Catalog;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using JellyServer.Api;
using JellyServer.Configuration;
using JellyServer.Scanning;
using Microsoft.Extensions.Options;
using JellyServer.Kernel;
using JellyServer.Kernel.Caching;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;

namespace JellyServer.Services;

/// <summary>Server ID and URL-signing secret, persisted in the data directory across restarts.</summary>
public sealed class ServerIdentity
{
    public string ServerId { get; }
    public Guid RootFolderId { get; }
    public byte[] Secret { get; }

    public ServerIdentity(IOptions<ServerOptions> options)
    {
        var dir = options.Value.DataDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "identity");
        if (File.Exists(path) && File.ReadAllLines(path) is [var id, var secret, ..] && id.Length == 32)
        {
            ServerId = id;
            Secret = Convert.FromHexString(secret);
        }
        else
        {
            ServerId = Guid.NewGuid().ToString("N");
            Secret = RandomNumberGenerator.GetBytes(32);
            File.WriteAllLines(path, [ServerId, Convert.ToHexString(Secret)]);
        }
        RootFolderId = Ids.FromKey("root", ServerId);
    }

    /// <summary>Short HMAC used to authorise proxied stream URLs without exposing tokens.</summary>
    public string Sign(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Secret, System.Text.Encoding.UTF8.GetBytes(value)))[..24];
}

/// <summary>Active client sessions, used by /Sessions and the dashboard.</summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    public sealed class SessionState
    {
        public required string Id { get; init; }
        public required Guid UserId { get; init; }
        public required string UserName { get; init; }
        public string Client { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Version { get; set; } = "";
        public DateTime LastActivity { get; set; }
        public Guid? NowPlayingItemId { get; set; }
        public long? PositionTicks { get; set; }
        public bool IsPaused { get; set; }
        public string? PlayMethod { get; set; }
    }

    public SessionState Touch(AuthInfo auth)
    {
        var key = string.IsNullOrEmpty(auth.Header.DeviceId) ? auth.Session.Token : auth.Header.DeviceId;
        var s = _sessions.GetOrAdd(key, k => new SessionState
        {
            Id = Ids.FromKey("session", k).N(), UserId = auth.User.Id, UserName = auth.User.Name,
        });
        s.Client = auth.Header.Client;
        s.DeviceName = auth.Header.Device;
        s.DeviceId = auth.Header.DeviceId;
        s.Version = auth.Header.Version;
        s.LastActivity = DateTime.UtcNow;
        return s;
    }

    public IEnumerable<SessionState> Active(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        foreach (var (key, s) in _sessions)
        {
            if (s.LastActivity < cutoff - TimeSpan.FromHours(1)) _sessions.TryRemove(key, out _);
            else if (s.LastActivity >= cutoff) yield return s;
        }
    }
}

/// <summary>Keeps the locally mirrored cache generation in sync when another replica scans.</summary>
public sealed class CacheGenerationSync(RedisCache cache) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do { await cache.RefreshGenerationAsync(); }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
