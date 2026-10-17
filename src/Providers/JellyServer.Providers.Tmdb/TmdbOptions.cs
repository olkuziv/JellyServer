namespace JellyServer.Providers.Tmdb;

/// <summary>
/// Bound from <c>JellyServer:Tmdb</c>. The key is also read from the plain <c>TMDB_API_KEY</c> environment variable;
/// either an API key (v3) or a read access token (v4, starts with "eyJ") works.
/// </summary>
public sealed class TmdbOptions
{
    public const string Section = "JellyServer:Tmdb";

    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/";

    /// <summary>TMDB allows about 50 requests per second; staying well below keeps shared connections happy.</summary>
    public int RequestsPerSecond { get; set; } = 50;

    /// <summary>Actors kept per item (Jellyfin's default is 15).</summary>
    public int MaxCast { get; set; } = 15;

    public bool IsToken => ApiKey is { Length: > 40 } || ApiKey?.StartsWith("eyJ", StringComparison.Ordinal) == true;
}