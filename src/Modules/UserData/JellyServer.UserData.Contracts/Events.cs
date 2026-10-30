namespace JellyServer.UserData.Contracts;

/// <summary>
/// A user's played/favorite state or resume position of these items was changed on purpose (marked played, favorited, playback stopped).
/// Not published for the periodic progress reports of a running playback.
/// </summary>
public sealed record UserDataChanged(Guid UserId, IReadOnlyList<Guid> ItemIds);
