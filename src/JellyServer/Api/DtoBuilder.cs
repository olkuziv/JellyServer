using System.Security.Cryptography;
using System.Text;
using JellyServer.Configuration;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using Microsoft.Extensions.Options;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.UserData.Contracts;

namespace JellyServer.Api;

/// <summary>Parsed <c>Fields=</c> parameter: heavy fields are opt-in to keep list pages small.</summary>
public sealed class FieldSet
{
    private readonly HashSet<string> _fields;
    private FieldSet(HashSet<string> f) => _fields = f;

    public static FieldSet Parse(HttpRequest req) => new(new HashSet<string>(req.Query.List("fields"), StringComparer.OrdinalIgnoreCase));

    public static readonly FieldSet All = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Overview", "Genres", "People", "Studios", "MediaSources", "MediaStreams", "Path", "Taglines", "ExternalUrls",
    });

    public bool Has(string f) => _fields.Contains(f);
}

/// <summary>
/// Converts domain items to Jellyfin DTOs. Each call issues a fixed number of batched
/// lookups (parents, user data, folder counts) regardless of page size: no N+1 queries.
/// </summary>
public sealed class DtoBuilder(ItemRepository items, IUserDataStore userStore, ServerIdentity identity, IOptions<ServerOptions> options, IOptionsMonitor<ServerOptions> monitor)
{
    private readonly PlaybackOptions _playback = options.Value.Playback;

    public string ServerId => identity.ServerId;

    public async Task<List<BaseItemDto>> BuildAsync(IReadOnlyList<Item> list, AuthInfo? auth, FieldSet fields, CancellationToken ct = default)
    {
        var parentIds = list.SelectMany(i => new[] { i.SeriesId, i.SeasonId }).OfType<Guid>().Distinct().ToList();
        var parents = (await items.GetManyAsync(parentIds, ct)).ToDictionary(p => p.Id);
        Dictionary<Guid, ItemUserData> userData = [];
        Dictionary<Guid, int> playedCounts = [];
        if (auth is not null)
        {
            // Two modules, possibly two databases: ask both at once.
            var dataTask = userStore.GetManyAsync(auth.User.Id, list.Select(i => i.Id).ToList(), ct);
            var countsTask = items.GetPlayedCountsAsync(auth.User.Id, list.Where(i => i.IsFolder).Select(i => i.Id).ToList(), ct);
            await Task.WhenAll(dataTask, countsTask);
            userData = await dataTask;
            playedCounts = await countsTask;
        }
        return list.Select(it => ToDto(it, auth, fields, parents, userData, playedCounts)).ToList();
    }

    public async Task<BaseItemDto> BuildAsync(Item item, AuthInfo? auth, FieldSet fields, CancellationToken ct = default) =>
        (await BuildAsync([item], auth, fields, ct))[0];

    public static string ImageTag(string source, string fingerprint) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(source + "|" + fingerprint)))[..16];

    private BaseItemDto ToDto(Item it, AuthInfo? auth, FieldSet f, Dictionary<Guid, Item> parents,
        Dictionary<Guid, ItemUserData> userData, Dictionary<Guid, int> playedCounts)
    {
        var id = it.Id.N();
        var d = new BaseItemDto
        {
            Name = it.Name, OriginalTitle = it.OriginalTitle, ServerId = ServerId, Id = id,
            Etag = it.Fingerprint.Length >= 16 ? it.Fingerprint[..16] : it.Fingerprint,
            DateCreated = it.DateCreated, CanDownload = !it.IsStrm, SortName = it.SortName,
            PremiereDate = it.PremiereDate, EndDate = it.EndDate, OfficialRating = it.OfficialRating,
            CommunityRating = it.CommunityRating, CriticRating = it.CriticRating,
            RunTimeTicks = it.RuntimeTicks > 0 ? it.RuntimeTicks : null, ProductionYear = it.ProductionYear,
            IndexNumber = it.IndexNumber, ParentIndexNumber = it.ParentIndexNumber, ProviderIds = it.ProviderIds,
            IsFolder = it.IsFolder, Type = it.Type,
            PrimaryImageAspectRatio = it.Type == ItemTypes.Episode ? 16.0 / 9.0 : 2.0 / 3.0,
        };
        if (f.Has("Overview")) d.Overview = it.Overview;
        if (f.Has("Genres"))
        {
            d.Genres = it.Genres;
            d.GenreItems = it.Genres.Select(g => new NameIdPair(g, Ids.GenreId(g).N())).ToList();
        }
        if (f.Has("Studios")) d.Studios = it.Studios.Select(s => new NameIdPair(s, Ids.FromKey("studio", s).N())).ToList();
        if (f.Has("Taglines") && it.Tagline is { } tl) d.Taglines = [tl];
        if (f.Has("Path")) d.Path = it.Path;
        if (f.Has("People") && it.People is { } people)
            d.People = people.Select(p => new PersonDto { Name = p.Name, Id = Ids.FromKey("person", p.Name).N(), Role = p.Role, Type = p.Type }).ToList();
        if (f.Has("ExternalUrls")) d.ExternalUrls = ExternalUrls(it);

        // Artwork tags change whenever the item's fingerprint changes, so clients can cache forever.
        if (it.Images.Primary is { } primary) d.ImageTags["Primary"] = ImageTag(primary, it.Fingerprint);
        foreach (var t in new[] { "Logo", "Thumb", "Banner" }.Concat(ItemImages.OtherTypes))
            if (it.Images.Get(t, 0) is { } src) d.ImageTags[t] = ImageTag(src, it.Fingerprint);
        d.BackdropImageTags = it.Images.Backdrops.Select(b => ImageTag(b, it.Fingerprint)).ToList();

        switch (it.Type)
        {
            case ItemTypes.Movie or ItemTypes.Episode:
                d.MediaType = "Video";
                d.VideoType = "VideoFile";
                d.Container = it.Container;
                d.MediaSourceCount = 1 + (it.Versions?.Count ?? 0);
                d.HasSubtitles = it.Subtitles is { Count: > 0 } || it.MediaInfo?.Subtitles.Count > 0;
                if (it.MediaInfo?.Video.FirstOrDefault() is { } v) (d.Width, d.Height) = (v.Width, v.Height);
                if (f.Has("MediaSources") || f.Has("MediaStreams"))
                {
                    var ms = MediaSource(it);
                    if (f.Has("MediaSources")) d.MediaSources = [ms, .. (it.Versions ?? []).Select(v => MediaSource(it.AsVersion(v)))];
                    d.MediaStreams = ms.MediaStreams;
                }
                d.Chapters = [];
                break;
            case ItemTypes.Series:
                d.ChildCount = it.ChildCount;
                d.RecursiveItemCount = it.RecursiveChildCount;
                d.DateLastMediaAdded = it.DateLastMediaAdded;
                d.Status = it.EndDate is null ? "Continuing" : "Ended";
                d.DisplayPreferencesId = id;
                break;
            case ItemTypes.Season:
                d.ChildCount = it.ChildCount;
                d.RecursiveItemCount = it.RecursiveChildCount;
                d.DisplayPreferencesId = id;
                break;
        }

        // Parent references let clients fall back to series artwork.
        if (it.SeriesId is { } seriesId)
        {
            d.SeriesId = d.ParentId = seriesId.N();
            if (parents.TryGetValue(seriesId, out var series))
            {
                d.SeriesName = series.Name;
                if (series.Images.Primary is { } sp) d.SeriesPrimaryImageTag = ImageTag(sp, series.Fingerprint);
                if (series.Images.Backdrops.Count > 0)
                {
                    d.ParentBackdropItemId = d.SeriesId;
                    d.ParentBackdropImageTags = series.Images.Backdrops.Select(b => ImageTag(b, series.Fingerprint)).ToList();
                }
                if (series.Images.Logo is { } logo)
                {
                    d.ParentLogoItemId = d.SeriesId;
                    d.ParentLogoImageTag = ImageTag(logo, series.Fingerprint);
                }
                if (series.Images.Thumb is { } thumb)
                {
                    d.ParentThumbItemId = d.SeriesId;
                    d.ParentThumbImageTag = ImageTag(thumb, series.Fingerprint);
                }
                if (it.Type == ItemTypes.Season && it.Images.Primary is null && d.SeriesPrimaryImageTag is not null)
                {
                    d.ParentPrimaryImageItemId = d.SeriesId;
                    d.ParentPrimaryImageTag = d.SeriesPrimaryImageTag;
                }
            }
        }
        if (it.SeasonId is { } seasonId)
        {
            d.SeasonId = d.ParentId = seasonId.N();
            if (parents.TryGetValue(seasonId, out var season)) d.SeasonName = season.Name;
        }
        d.ParentId ??= it.LibraryId.N();

        if (auth is not null) d.UserData = UserDataDto(it, userData.GetValueOrDefault(it.Id), playedCounts);
        return d;
    }

    public static UserItemDataDto UserDataDto(Item it, ItemUserData? ud, IReadOnlyDictionary<Guid, int>? playedCounts = null)
    {
        var dto = new UserItemDataDto { Key = it.Id.N(), ItemId = it.Id.N() };
        if (ud is not null)
        {
            dto.PlaybackPositionTicks = ud.PositionTicks;
            dto.PlayCount = ud.PlayCount;
            dto.IsFavorite = ud.IsFavorite;
            dto.Played = ud.Played;
            dto.LastPlayedDate = ud.LastPlayedAt;
            if (it.RuntimeTicks > 0 && ud.PositionTicks > 0)
                dto.PlayedPercentage = Math.Min(100, ud.PositionTicks * 100.0 / it.RuntimeTicks);
        }
        if (it.IsFolder && playedCounts is not null)
        {
            var unplayed = Math.Max(0, it.RecursiveChildCount - playedCounts.GetValueOrDefault(it.Id));
            dto.UnplayedItemCount = unplayed;
            dto.Played = it.RecursiveChildCount > 0 && unplayed == 0;
        }
        return dto;
    }

    private static List<ExternalUrl> ExternalUrls(Item it)
    {
        var urls = new List<ExternalUrl>();
        if (it.ProviderIds.TryGetValue("Imdb", out var imdb)) urls.Add(new("IMDb", $"https://www.imdb.com/title/{Uri.EscapeDataString(imdb)}"));
        if (it.ProviderIds.TryGetValue("Tmdb", out var tmdb))
            urls.Add(new("TheMovieDb", $"https://www.themoviedb.org/{(it.Type == ItemTypes.Series ? "tv" : "movie")}/{Uri.EscapeDataString(tmdb)}"));
        return urls;
    }

    /// <summary>A library rendered as a CollectionFolder ("view").</summary>
    public BaseItemDto LibraryDto(Library lib, int childCount)
    {
        var id = lib.Id.N();
        var dto = new BaseItemDto
        {
            Name = lib.Name, ServerId = ServerId, Id = id, Etag = id[..16], DateCreated = lib.CreatedAt,
            SortName = lib.Name.ToLowerInvariant(), IsFolder = true, Type = "CollectionFolder",
            CollectionType = LibraryTypes.ForClient(lib.CollectionType), ParentId = identity.RootFolderId.N(), DisplayPreferencesId = id,
            ChildCount = childCount, Path = string.Join(';', lib.Paths), PrimaryImageAspectRatio = 16.0 / 9.0,
            UserData = new UserItemDataDto { Key = id, ItemId = id },
        };
        foreach (var t in new[] { "Primary", "Logo", "Thumb", "Banner" }.Concat(ItemImages.OtherTypes))
            if (lib.Images.Get(t, 0) is { } src) dto.ImageTags[t] = ImageTag(src, "");
        dto.BackdropImageTags = lib.Images.Backdrops.Select(b => ImageTag(b, "")).ToList();
        return dto;
    }

    /// <summary>
    /// How a client should play an item:
    ///  - local files: DirectStream via /Videos/{id}/stream.{container}?Static=true;
    ///  - .strm "direct": Protocol=Http + DirectPlay only, so the client opens the URL itself.
    ///    IsRemote stays false on purpose: jellyfin-web refuses remote direct play on Tizen/webOS;
    ///  - .strm "redirect"/"proxy": DirectStream through this server.
    /// No transcoding is offered (yet), so clients never wait on a transcoder.
    /// </summary>
    public MediaSourceInfo MediaSource(Item it, string? client = null)
    {
        var ms = new MediaSourceInfo
        {
            Id = it.Id.N(), Path = it.Path, Container = it.Container, Size = it.Size > 0 ? it.Size : null,
            Name = Path.GetFileNameWithoutExtension(it.Path), ETag = it.Fingerprint,
            RunTimeTicks = it.RuntimeTicks > 0 ? it.RuntimeTicks : null,
            SupportsDirectPlay = true, SupportsDirectStream = true, SupportsProbing = true,
        };
        if (it.IsStrm)
        {
            ms.Protocol = "Http";
            ms.SupportsProbing = false;
            if (_playback.PlaysDirectly(client))
            {
                ms.Path = it.StreamUrl!;
                ms.SupportsDirectStream = false;
            }
            else
            {
                ms.SupportsDirectPlay = false;
            }
        }
        ms.MediaStreams = MediaStreams(it, ms);
        ApplySubtitleDelivery(ms, it, null, transcode: false, it.Container);
        return ms;
    }

    private static List<MediaStreamDto> MediaStreams(Item it, MediaSourceInfo ms)
    {
        var streams = new List<MediaStreamDto>();
        var index = 0;
        if (it.MediaInfo is { } mi)
        {
            foreach (var (v, i) in mi.Video.Select((v, i) => (v, i)))
            {
                var hdr = !string.IsNullOrEmpty(v.HdrType);
                streams.Add(new MediaStreamDto
                {
                    Codec = v.Codec, Type = "Video", Index = index++, Width = v.Width, Height = v.Height, IsDefault = i == 0,
                    VideoRange = hdr ? "HDR" : "SDR", VideoRangeType = hdr ? v.HdrType!.ToUpperInvariant() : "SDR",
                    AspectRatio = v.Aspect > 0 ? v.Aspect.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : null,
                    DisplayTitle = VideoTitle(v),
                });
            }
            foreach (var (a, i) in mi.Audio.Select((a, i) => (a, i)))
            {
                if (i == 0) ms.DefaultAudioStreamIndex = index;
                streams.Add(new MediaStreamDto
                {
                    Codec = a.Codec, Language = a.Language, Type = "Audio", Index = index++, Channels = a.Channels > 0 ? a.Channels : null,
                    IsDefault = i == 0, DisplayTitle = $"{LanguageName(a.Language)} {a.Codec.ToUpperInvariant()} {ChannelName(a.Channels)}".Trim(),
                });
            }
        }
        streams.AddRange(SubtitleTrack.List(it).Select(SubtitleStream));
        return streams;
    }

    private static MediaStreamDto SubtitleStream(SubtitleTrack t)
    {
        var e = t.Embedded;
        var d = new MediaStreamDto
        {
            Codec = t.Codec.Length > 0 ? t.Codec : null, Language = t.Language, Title = e?.Title, Type = "Subtitle", Index = t.Index,
            IsExternal = t.IsExternal, IsTextSubtitleStream = t.IsText, SupportsExternalStream = t.SupportsExternalStream,
            IsDefault = e?.IsDefault ?? false, IsForced = e?.IsForced ?? t.File!.Forced, IsHearingImpaired = e?.IsHearingImpaired ?? false,
            Path = t.File?.Path,
        };
        d.DisplayTitle = SubtitleTitle(d);
        return d;
    }

    /// <summary>Jellyfin's subtitle DisplayTitle: "Title - Language - Default - Forced - CODEC - External".</summary>
    private static string SubtitleTitle(MediaStreamDto s)
    {
        List<string> tags = [string.IsNullOrEmpty(s.Language) ? "Und" : LanguageName(s.Language)!];
        if (s.IsHearingImpaired) tags.Add("Hearing Impaired");
        if (s.IsDefault) tags.Add("Default");
        if (s.IsForced) tags.Add("Forced");
        if (!string.IsNullOrEmpty(s.Codec)) tags.Add(s.Codec.ToUpperInvariant());
        if (s.IsExternal) tags.Add("External");
        return string.IsNullOrEmpty(s.Title) ? string.Join(" - ", tags)
            : string.Concat(s.Title, string.Concat(tags.Where(t => !s.Title.Contains(t, StringComparison.OrdinalIgnoreCase)).Select(t => " - " + t)));
    }

    /// <summary>
    /// Sets DeliveryMethod/DeliveryUrl on the subtitle streams the way Jellyfin's PlaybackInfo does, from the client's
    /// SubtitleProfiles (jellyfin-web's defaults when it sent none). External streams point at our subtitle endpoint,
    /// which serves sidecars and extracts embedded streams on demand.
    /// </summary>
    /// <param name="transcode">The client gets an HLS remux/transcode instead of the file.</param>
    /// <param name="outputContainer">Segment container when transcoding, else the media container.</param>
    public void ApplySubtitleDelivery(MediaSourceInfo ms, Item it, IReadOnlyList<SubtitleProfileDto>? profiles, bool transcode, string? outputContainer)
    {
        var tracks = SubtitleTrack.List(it).ToDictionary(t => t.Index);
        var canExtract = monitor.CurrentValue.Transcoding.Subtitles.Extraction;
        foreach (var s in ms.MediaStreams)
        {
            if (s.Type != "Subtitle" || !tracks.TryGetValue(s.Index, out var track)) continue;
            var (method, format) = SubtitleDelivery.Decide(track, profiles is { Count: > 0 } ? profiles : SubtitleDelivery.DefaultProfiles,
                transcode, outputContainer, hls: transcode, canExtract);
            s.DeliveryMethod = method;
            s.DeliveryUrl = method == SubtitleDelivery.External ? SubtitleUrl(it, track.Index, format) : null;
            s.IsExternalUrl = method == SubtitleDelivery.External ? false : null;
        }
    }

    public static string SubtitleUrl(Item it, int index, string format) =>
        $"/Videos/{it.Id.N()}/{it.Id.N()}/Subtitles/{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}/0/Stream.{format.ToLowerInvariant()}";

    private static string VideoTitle(VideoStream v)
    {
        var res = v switch
        {
            { Width: >= 3800 } or { Height: >= 2100 } => "4K",
            { Width: >= 1900 } or { Height: >= 1000 } => "1080p",
            { Width: >= 1260 } or { Height: >= 700 } => "720p",
            { Height: > 0 } => $"{v.Height}p",
            _ => "",
        };
        return $"{res} {v.Codec.ToUpperInvariant()}".Trim();
    }

    private static string ChannelName(int channels) => channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", _ => "" };

    private static string LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        "ukr" or "uk" => "Ukrainian",
        "eng" or "en" => "English",
        "rus" or "ru" => "Russian",
        "jpn" or "ja" => "Japanese",
        "ger" or "deu" => "German",
        "fre" or "fra" => "French",
        "spa" => "Spanish",
        "ita" => "Italian",
        "pol" => "Polish",
        null or "" => "Unknown",
        _ => code,
    };
}
