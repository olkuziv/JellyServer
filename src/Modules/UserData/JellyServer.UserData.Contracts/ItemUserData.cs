namespace JellyServer.UserData.Contracts;

public sealed class ItemUserData
{
    public long PositionTicks { get; set; }
    public int PlayCount { get; set; }
    public bool Played { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime? LastPlayedAt { get; set; }
}
