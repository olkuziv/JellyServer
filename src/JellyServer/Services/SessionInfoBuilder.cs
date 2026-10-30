using JellyServer.Api;
using JellyServer.Catalog;
using JellyServer.Identity.Contracts;
using JellyServer.Kernel;

namespace JellyServer.Services;

/// <summary>The session list as clients see it: <c>GET /Sessions</c> and the <c>Sessions</c> WebSocket topic share it.</summary>
public sealed class SessionInfoBuilder(SessionRegistry sessions, ItemRepository items, DtoBuilder dto, ServerIdentity identity)
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);

    /// <summary>An administrator sees every session, anyone else only their own.</summary>
    public async Task<List<SessionInfoDto>> BuildAsync(User viewer, FieldSet fields, CancellationToken ct = default)
    {
        var active = sessions.Active(ActiveWindow).ToList();
        if (!viewer.IsAdmin) active = active.Where(s => s.UserId == viewer.Id).ToList();
        var nowPlaying = active.Select(s => s.NowPlayingItemId).OfType<Guid>().Distinct().ToList();
        var playingDtos = nowPlaying.Count == 0
            ? []
            : (await dto.BuildAsync(await items.GetManyAsync(nowPlaying), null, fields, ct)).ToDictionary(d => d.Id);
        return [.. active.Select(s => new SessionInfoDto
        {
            Id = s.Id, UserId = s.UserId.N(), UserName = s.UserName, Client = s.Client, DeviceName = s.DeviceName,
            DeviceId = s.DeviceId, ApplicationVersion = s.Version, LastActivityDate = s.LastActivity, ServerId = identity.ServerId,
            PlayState = new PlayStateDto { PositionTicks = s.PositionTicks, IsPaused = s.IsPaused, PlayMethod = s.PlayMethod },
            NowPlayingItem = s.NowPlayingItemId is { } nid ? playingDtos.GetValueOrDefault(nid.N()) : null,
        })];
    }
}
