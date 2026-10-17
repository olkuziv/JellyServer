using System.Text.Json;
using System.Text.Json.Serialization;
using JellyServer.Api;
using JellyServer.Api.Endpoints;
using JellyServer.Services;
using JellyServer.Scanning;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;

namespace JellyServer.Serialization;

/// <summary>
/// Source-generated JSON metadata: no runtime reflection on the hot path.
/// Property names are emitted as declared (PascalCase), as Jellyfin clients expect.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
// cache / storage
// API
[JsonSerializable(typeof(BaseItemDto))]
[JsonSerializable(typeof(List<BaseItemDto>))]
[JsonSerializable(typeof(QueryResult<BaseItemDto>))]
[JsonSerializable(typeof(UserItemDataDto))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<UserDto>))]
[JsonSerializable(typeof(AuthenticationResult))]
[JsonSerializable(typeof(SessionInfoDto))]
[JsonSerializable(typeof(List<SessionInfoDto>))]
[JsonSerializable(typeof(List<ScheduledTaskInfo>))]
[JsonSerializable(typeof(ScheduledTaskInfo))]
[JsonSerializable(typeof(List<TaskTriggerInfo>))]
[JsonSerializable(typeof(PlaybackInfoResponse))]
[JsonSerializable(typeof(PublicSystemInfo))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(DisplayPreferencesDto))]
[JsonSerializable(typeof(List<VirtualFolderInfo>))]
[JsonSerializable(typeof(SearchHintResult))]
[JsonSerializable(typeof(List<ImageInfoDto>))]
[JsonSerializable(typeof(QueryFiltersLegacy))]
[JsonSerializable(typeof(QueryFilters))]
[JsonSerializable(typeof(ItemCounts))]
[JsonSerializable(typeof(ThemeMediaResult))]
[JsonSerializable(typeof(BrandingOptions))]
[JsonSerializable(typeof(EndpointInfo))]
[JsonSerializable(typeof(SystemStorageDto))]
[JsonSerializable(typeof(LiveTvInfo))]
[JsonSerializable(typeof(WebSocketMessage))]
[JsonSerializable(typeof(ScanStatus))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(AuthenticateByName))]
[JsonSerializable(typeof(PlaybackInfoRequest))]
[JsonSerializable(typeof(DeviceProfileDto))]
[JsonSerializable(typeof(PlaybackReport))]
[JsonSerializable(typeof(UserDataUpdate))]
[JsonSerializable(typeof(PasswordChange))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(SubtitleTrackEvents))]
[JsonSerializable(typeof(List<FontFile>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
public sealed partial class AppJson : JsonSerializerContext;
