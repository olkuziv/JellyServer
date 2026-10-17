using System.Text.Json;
using JellyServer.Identity.Contracts;

namespace JellyServer.Api;

// Shapes follow Jellyfin's OpenAPI DTOs; only fields clients actually read are emitted.

public sealed class BaseItemDto
{
    public string Name { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string ServerId { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Etag { get; set; }
    public DateTime? DateCreated { get; set; }
    public DateTime? DateLastMediaAdded { get; set; }
    public bool CanDelete { get; set; }
    public bool CanDownload { get; set; }
    public string? SortName { get; set; }
    public DateTime? PremiereDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<ExternalUrl>? ExternalUrls { get; set; }
    public string? Path { get; set; }
    public string? OfficialRating { get; set; }
    public string? Overview { get; set; }
    public List<string>? Taglines { get; set; }
    public string[]? Genres { get; set; }
    public float? CommunityRating { get; set; }
    public float? CriticRating { get; set; }
    public long? RunTimeTicks { get; set; }
    public int? ProductionYear { get; set; }
    public int? IndexNumber { get; set; }
    public int? ParentIndexNumber { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public bool IsFolder { get; set; }
    public string? ParentId { get; set; }
    public string Type { get; set; } = "";
    public List<PersonDto>? People { get; set; }
    public List<NameIdPair>? Studios { get; set; }
    public List<NameIdPair>? GenreItems { get; set; }
    public UserItemDataDto? UserData { get; set; }
    public int? ChildCount { get; set; }
    public int? RecursiveItemCount { get; set; }
    public string? SeriesName { get; set; }
    public string? SeriesId { get; set; }
    public string? SeasonId { get; set; }
    public string? SeasonName { get; set; }
    public string? SeriesPrimaryImageTag { get; set; }
    public string? ParentBackdropItemId { get; set; }
    public List<string>? ParentBackdropImageTags { get; set; }
    public string? ParentLogoItemId { get; set; }
    public string? ParentLogoImageTag { get; set; }
    public string? ParentThumbItemId { get; set; }
    public string? ParentThumbImageTag { get; set; }
    public string? ParentPrimaryImageItemId { get; set; }
    public string? ParentPrimaryImageTag { get; set; }
    public double? PrimaryImageAspectRatio { get; set; }
    public string? CollectionType { get; set; }
    public string? DisplayPreferencesId { get; set; }
    public Dictionary<string, string> ImageTags { get; set; } = [];
    public List<string> BackdropImageTags { get; set; } = [];
    public string LocationType { get; set; } = "FileSystem";
    public string MediaType { get; set; } = "Unknown";
    public string? VideoType { get; set; }
    public string? Container { get; set; }
    public List<MediaSourceInfo>? MediaSources { get; set; }
    public List<MediaStreamDto>? MediaStreams { get; set; }
    public int? MediaSourceCount { get; set; }
    public bool? HasSubtitles { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Status { get; set; }
    public string PlayAccess { get; set; } = "Full";
    public bool EnableMediaSourceDisplay { get; set; } = true;
    public int LocalTrailerCount { get; set; }
    public int SpecialFeatureCount { get; set; }
    public List<object> RemoteTrailers { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> LockedFields { get; set; } = [];
    public bool LockData { get; set; }
    public List<object>? Chapters { get; set; }
    /// <summary>The line of a playlist this item is (Jellyfin: the item id of the linked child).</summary>
    public string? PlaylistItemId { get; set; }
    public long? CumulativeRunTimeTicks { get; set; }
    public string? DisplayOrder { get; set; }
}

public sealed record ExternalUrl(string Name, string Url);
public sealed record NameIdPair(string Name, string Id);

public sealed class PersonDto
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Role { get; set; }
    public string Type { get; set; } = "Actor";
    public string? PrimaryImageTag { get; set; }
    public Dictionary<string, string> ImageBlurHashes { get; set; } = [];
}

// ---- playlists and collections (Jellyfin.Api.Models.PlaylistDtos, MediaBrowser.Model.Playlists/Collections) ----

public sealed class PlaylistCreationResult
{
    public string Id { get; set; } = "";
}

public sealed class CollectionCreationResult
{
    public string Id { get; set; } = "";
}

public sealed class PlaylistUserPermissionsDto
{
    public string UserId { get; set; } = "";
    public bool CanEdit { get; set; }
}

public sealed class PlaylistDto
{
    public bool OpenAccess { get; set; }
    public List<PlaylistUserPermissionsDto> Shares { get; set; } = [];
    public List<string> ItemIds { get; set; } = [];
}

public sealed class CreatePlaylistDto
{
    public string? Name { get; set; }
    public List<string>? Ids { get; set; }
    public string? UserId { get; set; }
    public string? MediaType { get; set; }
    public List<PlaylistUserPermissionsDto>? Users { get; set; }
    public bool? IsPublic { get; set; }
}

public sealed class UpdatePlaylistDto
{
    public string? Name { get; set; }
    public List<string>? Ids { get; set; }
    public List<PlaylistUserPermissionsDto>? Users { get; set; }
    public bool? IsPublic { get; set; }
}

public sealed class UpdatePlaylistUserDto
{
    public bool? CanEdit { get; set; }
}

public sealed class UserItemDataDto
{
    public long PlaybackPositionTicks { get; set; }
    public int PlayCount { get; set; }
    public bool IsFavorite { get; set; }
    public bool Played { get; set; }
    public string Key { get; set; } = "";
    public string ItemId { get; set; } = "";
    public DateTime? LastPlayedDate { get; set; }
    public double? PlayedPercentage { get; set; }
    public int? UnplayedItemCount { get; set; }
}

public sealed class QueryResult<T>
{
    public List<T> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
    public int StartIndex { get; set; }

    public static QueryResult<T> Empty => new();
}

/// <summary>Subset of Jellyfin's scheduled-task shape used by the dashboard.</summary>
public sealed class ScheduledTaskInfo
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "Idle";
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsHidden { get; set; }
    public bool IsEnabled { get; set; } = true;
    public List<TaskTriggerInfo> Triggers { get; set; } = [];
    public double? CurrentProgressPercentage { get; set; }
    public ScheduledTaskExecutionInfo? LastExecutionResult { get; set; }
}

public sealed class ScheduledTaskExecutionInfo
{
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public string Status { get; set; } = "Completed";
    public string? Name { get; set; }
    public string? Key { get; set; }
    public string? Id { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LongErrorMessage { get; set; }
}

/// <summary>Jellyfin's TaskTriggerInfo: DailyTrigger / WeeklyTrigger / IntervalTrigger / StartupTrigger.</summary>
public sealed class TaskTriggerInfo
{
    public string Type { get; set; } = "";
    public long? TimeOfDayTicks { get; set; }
    public long? IntervalTicks { get; set; }
    public string? DayOfWeek { get; set; }
    public long? MaxRuntimeTicks { get; set; }
}
public sealed class MediaSourceInfo
{
    public string Protocol { get; set; } = "File";
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "Default";
    public string? Container { get; set; }
    public long? Size { get; set; }
    public string Name { get; set; } = "";
    public bool IsRemote { get; set; }
    public string? ETag { get; set; }
    public long? RunTimeTicks { get; set; }
    public bool ReadAtNativeFramerate { get; set; }
    public bool IgnoreDts { get; set; }
    public bool IgnoreIndex { get; set; }
    public bool GenPtsInput { get; set; }
    public bool SupportsTranscoding { get; set; }
    public bool SupportsDirectStream { get; set; }
    public bool SupportsDirectPlay { get; set; }
    public bool IsInfiniteStream { get; set; }
    public bool RequiresOpening { get; set; }
    public bool RequiresClosing { get; set; }
    public bool RequiresLooping { get; set; }
    public bool SupportsProbing { get; set; }
    public string VideoType { get; set; } = "VideoFile";
    public List<MediaStreamDto> MediaStreams { get; set; } = [];
    public List<object> MediaAttachments { get; set; } = [];
    public List<string> Formats { get; set; } = [];
    public int? Bitrate { get; set; }
    public Dictionary<string, string> RequiredHttpHeaders { get; set; } = [];
    public string TranscodingSubProtocol { get; set; } = "http";
    public string? TranscodingUrl { get; set; }
    public string? TranscodingContainer { get; set; }
    public int? DefaultAudioStreamIndex { get; set; }
    public int? DefaultSubtitleStreamIndex { get; set; }
    public bool HasSegments { get; set; }
}

public sealed class MediaStreamDto
{
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public string DisplayTitle { get; set; } = "";
    public bool IsInterlaced { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsHearingImpaired { get; set; }
    public int? Height { get; set; }
    public int? Width { get; set; }
    public string? AspectRatio { get; set; }
    public int? Channels { get; set; }
    public string Type { get; set; } = "Video";
    public int Index { get; set; }
    public bool IsExternal { get; set; }
    public bool IsTextSubtitleStream { get; set; }
    public bool SupportsExternalStream { get; set; }
    public string? DeliveryMethod { get; set; }
    public string? DeliveryUrl { get; set; }
    public bool? IsExternalUrl { get; set; }
    public string? Path { get; set; }
    public string? VideoRange { get; set; }
    public string? VideoRangeType { get; set; }
    public int Level { get; set; }
}

public sealed class PlaybackInfoResponse
{
    public List<MediaSourceInfo> MediaSources { get; set; } = [];
    public string PlaySessionId { get; set; } = "";
}

public sealed class UserDto
{
    public string Name { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string Id { get; set; } = "";
    public bool HasPassword { get; set; }
    public bool HasConfiguredPassword { get; set; }
    public bool HasConfiguredEasyPassword { get; set; }
    public bool EnableAutoLogin { get; set; }
    public DateTime? LastLoginDate { get; set; }
    public DateTime? LastActivityDate { get; set; }
    public JsonElement Configuration { get; set; }
    public UserPolicy Policy { get; set; } = new();
}

public sealed class UserPolicy
{
    public bool IsAdministrator { get; set; }
    public bool IsHidden { get; set; }
    public bool IsDisabled { get; set; }
    public bool EnableAllFolders { get; set; } = true;
    public List<string> EnabledFolders { get; set; } = [];
    public bool EnableMediaPlayback { get; set; } = true;
    public bool EnableAudioPlaybackTranscoding { get; set; } = true;
    public bool EnableVideoPlaybackTranscoding { get; set; } = true;
    public bool EnablePlaybackRemuxing { get; set; } = true;
    public bool ForceRemoteSourceTranscoding { get; set; }
    public bool EnableContentDeletion { get; set; }
    public List<string> EnableContentDeletionFromFolders { get; set; } = [];
    public bool EnableContentDownloading { get; set; } = true;
    public bool EnableRemoteAccess { get; set; } = true;
    public bool EnableLiveTvAccess { get; set; }
    public bool EnableLiveTvManagement { get; set; }
    public bool EnableSharedDeviceControl { get; set; } = true;
    public bool EnableRemoteControlOfOtherUsers { get; set; }
    public bool EnablePublicSharing { get; set; }
    public bool EnableUserPreferenceAccess { get; set; } = true;
    public bool EnableSubtitleManagement { get; set; }
    public bool EnableLyricManagement { get; set; }
    public bool EnableCollectionManagement { get; set; }
    public bool EnableSyncTranscoding { get; set; }
    public bool EnableMediaConversion { get; set; }
    public bool EnableAllDevices { get; set; } = true;
    public bool EnableAllChannels { get; set; } = true;
    public List<string> EnabledDevices { get; set; } = [];
    public List<string> EnabledChannels { get; set; } = [];
    public List<string> BlockedTags { get; set; } = [];
    public List<string> AllowedTags { get; set; } = [];
    public List<object> AccessSchedules { get; set; } = [];
    public List<string> BlockUnratedItems { get; set; } = [];
    public int InvalidLoginAttemptCount { get; set; }
    public int LoginAttemptsBeforeLockout { get; set; } = -1;
    public int MaxActiveSessions { get; set; }
    public int RemoteClientBitrateLimit { get; set; }
    public string SyncPlayAccess { get; set; } = "CreateAndJoinGroups";
    public string AuthenticationProviderId { get; set; } = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
    public string PasswordResetProviderId { get; set; } = "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider";
}

public sealed class AuthenticationResult
{
    public UserDto User { get; set; } = new();
    public SessionInfoDto SessionInfo { get; set; } = new();
    public string AccessToken { get; set; } = "";
    public string ServerId { get; set; } = "";
}

public sealed class SessionInfoDto
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Client { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public DateTime LastActivityDate { get; set; }
    public bool IsActive { get; set; } = true;
    public bool SupportsMediaControl { get; set; }
    public bool SupportsRemoteControl { get; set; }
    public List<string> PlayableMediaTypes { get; set; } = ["Video", "Audio"];
    public List<string> SupportedCommands { get; set; } = [];
    public PlayStateDto PlayState { get; set; } = new();
    public BaseItemDto? NowPlayingItem { get; set; }
    public string ServerId { get; set; } = "";
    public List<object> AdditionalUsers { get; set; } = [];
    public bool HasCustomDeviceName { get; set; }
}

public sealed class PlayStateDto
{
    public long? PositionTicks { get; set; }
    public bool CanSeek { get; set; } = true;
    public bool IsPaused { get; set; }
    public bool IsMuted { get; set; }
    public string? PlayMethod { get; set; }
    public string RepeatMode { get; set; } = "RepeatNone";
}

public sealed class PublicSystemInfo
{
    public string LocalAddress { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProductName { get; set; } = "Jellyfin Server";
    public string OperatingSystem { get; set; } = "Linux";
    public string Id { get; set; } = "";
    public bool StartupWizardCompleted { get; set; } = true;
}

public sealed class SystemInfo
{
    public string LocalAddress { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProductName { get; set; } = "Jellyfin Server";
    public string OperatingSystem { get; set; } = "Linux";
    public string OperatingSystemDisplayName { get; set; } = "Linux";
    public string Id { get; set; } = "";
    public bool StartupWizardCompleted { get; set; } = true;
    public bool HasPendingRestart { get; set; }
    public bool IsShuttingDown { get; set; }
    public bool SupportsLibraryMonitor { get; set; }
    public int WebSocketPortNumber { get; set; }
    public List<object> CompletedInstallations { get; set; } = [];
    public bool CanSelfRestart { get; set; }
    public bool CanLaunchWebBrowser { get; set; }
    public string ProgramDataPath { get; set; } = "";
    public string WebPath { get; set; } = "";
    public string ItemsByNamePath { get; set; } = "";
    public string CachePath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string InternalMetadataPath { get; set; } = "";
    public string TranscodingTempPath { get; set; } = "";
    public List<object> CastReceiverApplications { get; set; } = [];
    public bool HasUpdateAvailable { get; set; }
    public string EncoderLocation { get; set; } = "System";
    public string SystemArchitecture { get; set; } = "X64";
}

public sealed class DisplayPreferencesDto
{
    public string Id { get; set; } = "";
    public string? ViewType { get; set; }
    public string SortBy { get; set; } = "SortName";
    public string? IndexBy { get; set; }
    public bool RememberIndexing { get; set; }
    public int PrimaryImageHeight { get; set; } = 250;
    public int PrimaryImageWidth { get; set; } = 250;
    public Dictionary<string, string?> CustomPrefs { get; set; } = [];
    public string ScrollDirection { get; set; } = "Horizontal";
    public bool ShowBackdrop { get; set; } = true;
    public bool RememberSorting { get; set; }
    public string SortOrder { get; set; } = "Ascending";
    public bool ShowSidebar { get; set; }
    public string Client { get; set; } = "emby";
}

public sealed class VirtualFolderInfo
{
    public string Name { get; set; } = "";
    public List<string> Locations { get; set; } = [];
    public string CollectionType { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string RefreshStatus { get; set; } = "Idle";
    public Dictionary<string, object> LibraryOptions { get; set; } = [];
}

public sealed class SearchHint
{
    public string ItemId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? ProductionYear { get; set; }
    public string? PrimaryImageTag { get; set; }
    public string? ThumbImageTag { get; set; }
    public string? ThumbImageItemId { get; set; }
    public string? BackdropImageTag { get; set; }
    public string? BackdropImageItemId { get; set; }
    public string Type { get; set; } = "";
    public bool IsFolder { get; set; }
    public long? RunTimeTicks { get; set; }
    public string MediaType { get; set; } = "Unknown";
    public string? Series { get; set; }
    public int? IndexNumber { get; set; }
    public int? ParentIndexNumber { get; set; }
    public double? PrimaryImageAspectRatio { get; set; }
}

public sealed class SearchHintResult
{
    public List<SearchHint> SearchHints { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public sealed class ImageInfoDto
{
    public string ImageType { get; set; } = "";
    public int? ImageIndex { get; set; }
    public string ImageTag { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class QueryFiltersLegacy
{
    public List<string> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> OfficialRatings { get; set; } = [];
    public List<int> Years { get; set; } = [];
}

public sealed class QueryFilters
{
    public List<NameIdPair> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}

public sealed class ItemCounts
{
    public int MovieCount { get; set; }
    public int SeriesCount { get; set; }
    public int EpisodeCount { get; set; }
    public int ArtistCount { get; set; }
    public int ProgramCount { get; set; }
    public int TrailerCount { get; set; }
    public int SongCount { get; set; }
    public int AlbumCount { get; set; }
    public int MusicVideoCount { get; set; }
    public int BoxSetCount { get; set; }
    public int BookCount { get; set; }
    public int ItemCount { get; set; }
}

public sealed class ThemeMediaResult
{
    public QueryResult<BaseItemDto> ThemeVideosResult { get; set; } = new();
    public QueryResult<BaseItemDto> ThemeSongsResult { get; set; } = new();
    public QueryResult<BaseItemDto> SoundtrackSongsResult { get; set; } = new();
}

public sealed class BrandingOptions
{
    public string LoginDisclaimer { get; set; } = "";
    public string CustomCss { get; set; } = "";
    public bool SplashscreenEnabled { get; set; }
}

/// <summary>Jellyfin's FolderStorageDto: where a folder lives and how full its disk is.</summary>
public sealed class FolderStorageDto
{
    public string Path { get; set; } = "";
    public long FreeSpace { get; set; }
    public long UsedSpace { get; set; }
    public string? StorageType { get; set; }
    public string? DeviceId { get; set; }
}

public sealed class LibraryStorageDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<FolderStorageDto> Folders { get; set; } = [];
}

/// <summary>Jellyfin's SystemStorageDto (GET /System/Info/Storage), shown on the dashboard.</summary>
public sealed class SystemStorageDto
{
    public FolderStorageDto ProgramDataFolder { get; set; } = new();
    public FolderStorageDto WebFolder { get; set; } = new();
    public FolderStorageDto ImageCacheFolder { get; set; } = new();
    public FolderStorageDto CacheFolder { get; set; } = new();
    public FolderStorageDto LogFolder { get; set; } = new();
    public FolderStorageDto InternalMetadataFolder { get; set; } = new();
    public FolderStorageDto TranscodingTempFolder { get; set; } = new();
    public List<LibraryStorageDto> Libraries { get; set; } = [];
}

public sealed class EndpointInfo
{
    public bool IsLocal { get; set; }
    public bool IsInNetwork { get; set; }
}

public sealed class LiveTvInfo
{
    public List<object> Services { get; set; } = [];
    public bool IsEnabled { get; set; }
    public List<string> EnabledUsers { get; set; } = [];
}

public sealed class WebSocketMessage
{
    public string MessageType { get; set; } = "";
    public string MessageId { get; set; } = "";
    public JsonElement? Data { get; set; }
}

// ---- request bodies ----

public sealed class AuthenticateByName
{
    public string? Username { get; set; }
    public string? Pw { get; set; }
}

public sealed class PlaybackInfoRequest
{
    public string? UserId { get; set; }
    public long? MaxStreamingBitrate { get; set; }
    public long? StartTimeTicks { get; set; }
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public string? MediaSourceId { get; set; }
    public bool? EnableDirectPlay { get; set; }
    public bool? EnableDirectStream { get; set; }
    public bool? EnableTranscoding { get; set; }
    public bool? AllowVideoStreamCopy { get; set; }
    public bool? AllowAudioStreamCopy { get; set; }
    public DeviceProfileDto? DeviceProfile { get; set; }
}

/// <summary>What the client can play, as sent by jellyfin-web/apps in PlaybackInfo.</summary>
public sealed class DeviceProfileDto
{
    public List<DirectPlayProfileDto>? DirectPlayProfiles { get; set; }
    public List<TranscodingProfileDto>? TranscodingProfiles { get; set; }
    public List<JellyServer.Services.SubtitleProfileDto>? SubtitleProfiles { get; set; }
}

public sealed class DirectPlayProfileDto
{
    public string? Container { get; set; }
    public string? Type { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
}

public sealed class TranscodingProfileDto
{
    public string? Container { get; set; }
    public string? Type { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Protocol { get; set; }
    public string? Context { get; set; }
    public string? MaxAudioChannels { get; set; }
}

public sealed class PlaybackReport
{
    public string? ItemId { get; set; }
    public long? PositionTicks { get; set; }
    public bool IsPaused { get; set; }
    public string? PlayMethod { get; set; }
    public string? PlaySessionId { get; set; }
    public string? MediaSourceId { get; set; }
}

public sealed class UserDataUpdate
{
    public long? PlaybackPositionTicks { get; set; }
    public int? PlayCount { get; set; }
    public bool? IsFavorite { get; set; }
    public bool? Played { get; set; }
}

public sealed class PasswordChange
{
    public string? CurrentPw { get; set; }
    public string? NewPw { get; set; }
    public bool ResetPassword { get; set; }
}

public sealed class CreateUserRequest
{
    public string? Name { get; set; }
    public string? Password { get; set; }
}
