using System.Text.Json.Serialization;

namespace JellyServer.Identity.Contracts;

public sealed class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    [JsonIgnore] public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsHidden { get; set; }
    public string Configuration { get; set; } = "{}";
    /// <summary>Jellyfin's UserPolicy as JSON; "{}" means every default.</summary>
    public string Policy { get; set; } = "{}";
    public bool IsDisabled { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
}

public sealed class AuthSession
{
    public string Token { get; set; } = "";
    public Guid UserId { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Client { get; set; } = "";
    public string AppVersion { get; set; } = "";
}

/// <summary>A user was removed: modules holding per-user data clean up after it.</summary>
public sealed record UserDeleted(Guid UserId);
