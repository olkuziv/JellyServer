using System.Text.Json.Serialization;
using JellyServer.Media.Contracts;

namespace JellyServer.Media;

/// <summary>Source-generated JSON for the jsonb columns.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MediaInfo))]
[JsonSerializable(typeof(List<SubtitleFile>))]
public sealed partial class MediaJson : JsonSerializerContext;
