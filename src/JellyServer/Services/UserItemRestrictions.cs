using JellyServer.Api;
using JellyServer.Api.Endpoints;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity;
using JellyServer.Kernel;
using JellyServer.Localization;
using JellyServer.Settings.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace JellyServer.Services;

/// <summary>
/// What a user may see, from their policy: the libraries they were given and Jellyfin's parental control (a rating limit and
/// blocking of unrated items). The Catalog module applies the result to every query made for the user.
/// </summary>
public sealed class UserItemRestrictions(UserRepository users, LocalizationService localization, IServerConfiguration config, IMemoryCache memory)
    : IItemRestrictionProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    // Policy "BlockUnratedItems" kinds that the catalog holds, with the item types they cover.
    private static readonly Dictionary<string, string[]> UnratedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Movie"] = [ItemTypes.Movie],
        ["Series"] = [ItemTypes.Series, ItemTypes.Season, ItemTypes.Episode],
    };

    public static string CacheKey(Guid userId) => "restrictions:" + userId.ToString("N");

    /// <summary>Forgets the cached answer after the user's policy changed.</summary>
    public void Evict(Guid userId) => memory.Remove(CacheKey(userId));

    public async ValueTask<ItemRestrictions> GetAsync(Guid userId, CancellationToken ct = default)
    {
        if (memory.TryGetValue(CacheKey(userId), out ItemRestrictions? hit) && hit is not null) return hit;
        var result = await BuildAsync(userId, ct);
        memory.Set(CacheKey(userId), result, Ttl);
        return result;
    }

    private async Task<ItemRestrictions> BuildAsync(Guid userId, CancellationToken ct)
    {
        // A user who no longer exists sees nothing.
        if (await users.ByIdAsync(userId) is not { } user) return new ItemRestrictions(Libraries: []);
        var policy = UserEndpoints.ParsePolicy(user);

        var libraries = policy.EnableAllFolders ? null : policy.EnabledFolders.Select(Ids.Parse).OfType<Guid>().ToArray();

        string[]? blocked = null, known = null, unrated = null;
        var unratedTypes = policy.BlockUnratedItems.Where(UnratedKinds.ContainsKey).SelectMany(k => UnratedKinds[k]).Distinct().ToArray();
        if (policy.MaxParentalRating is not null || unratedTypes.Length > 0)
        {
            var country = (await config.GetAsync(ct))["MetadataCountryCode"]?.GetValue<string>();
            var scale = localization.RatingScale(country);
            known = [.. scale.Keys.Select(k => k.ToLowerInvariant())];
            if (policy.MaxParentalRating is { } max)
            {
                var sub = policy.MaxParentalSubRating;
                blocked = [.. scale.Where(r => r.Value.Score > max || (r.Value.Score == max && sub is not null && r.Value.SubScore > sub))
                    .Select(r => r.Key.ToLowerInvariant()).Distinct()];
            }
            unrated = unratedTypes.Length > 0 ? unratedTypes : null;
        }
        return new ItemRestrictions(libraries, blocked, known, unrated);
    }
}
