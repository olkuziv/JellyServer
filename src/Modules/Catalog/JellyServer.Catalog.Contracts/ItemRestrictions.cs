namespace JellyServer.Catalog.Contracts;

/// <summary>
/// What a user may see of the catalog (Jellyfin's library access and parental control). The Catalog module applies it to every
/// query made for a user, so no endpoint can forget it; the host decides what a user's policy means.
/// </summary>
/// <param name="Libraries">The libraries the user may open; null for all of them.</param>
/// <param name="BlockedRatings">Lower-cased rating strings above the user's limit.</param>
/// <param name="KnownRatings">Every lower-cased rating string the parental scale recognises (anything else counts as unrated).</param>
/// <param name="UnratedBlockedTypes">Item types whose unrated items are hidden.</param>
public sealed record ItemRestrictions(
    Guid[]? Libraries = null, string[]? BlockedRatings = null, string[]? KnownRatings = null, string[]? UnratedBlockedTypes = null)
{
    public static readonly ItemRestrictions None = new();

    public bool IsNone => Libraries is null && BlockedRatings is not { Length: > 0 } && UnratedBlockedTypes is not { Length: > 0 };

    public bool AllowsLibrary(Guid libraryId) => Libraries is null || Array.IndexOf(Libraries, libraryId) >= 0;

    /// <summary>Identifies the restrictions in cache keys.</summary>
    public string Key => IsNone ? "" : string.Join('/',
        Libraries is null ? "*" : string.Join(',', Libraries.Order().Select(l => l.ToString("N"))),
        BlockedRatings is null ? "" : string.Join(',', BlockedRatings.Order()),
        UnratedBlockedTypes is null ? "" : string.Join(',', UnratedBlockedTypes.Order()));
}

/// <summary>Answers for a user which items they may see; implemented by the host, which knows the user's policy.</summary>
public interface IItemRestrictionProvider
{
    ValueTask<ItemRestrictions> GetAsync(Guid userId, CancellationToken ct = default);
}
