using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;

namespace JellyServer.Collections.Contracts;

/// <summary>Jellyfin's item types for the two kinds of hand-made lists.</summary>
public static class CollectionKinds
{
    /// <summary>A user's ordered list of things to play; private unless shared or public.</summary>
    public const string Playlist = "Playlist";

    /// <summary>A collection ("box set") of movies or shows, the same for everyone.</summary>
    public const string BoxSet = "BoxSet";

    public static bool IsKind(string? type) =>
        string.Equals(type, Playlist, StringComparison.OrdinalIgnoreCase) || string.Equals(type, BoxSet, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string type) => string.Equals(type, Playlist, StringComparison.OrdinalIgnoreCase) ? Playlist : BoxSet;
}

/// <summary>A user a playlist is shared with (Jellyfin's PlaylistUserPermissions).</summary>
public sealed record CollectionShare(Guid UserId, bool CanEdit);

public sealed class Collection
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = CollectionKinds.Playlist;
    public string Name { get; set; } = "";

    /// <summary>Playlists: the user who made it (the only one who may share it or change its users).</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Playlists: every user may see and play it (Jellyfin's OpenAccess, "public").</summary>
    public bool OpenAccess { get; set; }

    public List<CollectionShare> Shares { get; set; } = [];

    /// <summary>Playlists: Video or Audio (Jellyfin picks it from the first item, Audio when nothing tells).</summary>
    public string? MediaType { get; set; }

    /// <summary>Box sets: internet metadata is not wanted.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Uploaded artwork; without it clients see the first item's.</summary>
    public ItemImages Images { get; set; } = new();

    public DateTime DateCreated { get; set; }
    public DateTime DateModified { get; set; }
    public DateTime? DateLastMediaAdded { get; set; }

    /// <summary>Number of entries.</summary>
    public int ItemCount { get; set; }

    public bool IsPlaylist => Kind == CollectionKinds.Playlist;

    /// <summary>Who sees it: box sets everybody; playlists their owner, the users it is shared with, or everyone when public.</summary>
    public bool IsVisibleTo(Guid userId) =>
        !IsPlaylist || OpenAccess || OwnerId == userId || Shares.Any(s => s.UserId == userId);

    /// <summary>Who may change the entries and the name (box sets: whoever may manage collections, decided by the caller).</summary>
    public bool CanEdit(Guid userId) => IsPlaylist && (OwnerId == userId || Shares.Any(s => s.UserId == userId && s.CanEdit));

    /// <summary>The fingerprint image tags are built from: new artwork or entries give new tags.</summary>
    public string Fingerprint => DateModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public static class CollectionViews
{
    /// <summary>The "Playlists" folder in the user's views (Jellyfin's ManualPlaylistsFolder).</summary>
    public static readonly Guid PlaylistsId = Ids.FromKey("view", "playlists");

    /// <summary>The "Collections" library (Jellyfin creates it as a boxsets CollectionFolder with the first collection).</summary>
    public static readonly Guid BoxSetsId = Ids.FromKey("view", "boxsets");
}
