using System.Text.Json.Serialization;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>Source-generated JSON for catalog rows (jsonb columns) and the Redis cache.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(RemoteMetadata))]
[JsonSerializable(typeof(QueryPage))]
[JsonSerializable(typeof(List<GenreCount>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<Person>))]
[JsonSerializable(typeof(MediaInfo))]
[JsonSerializable(typeof(ItemImages))]
[JsonSerializable(typeof(List<SubtitleFile>))]
[JsonSerializable(typeof(List<ItemVersion>))]
public sealed partial class CatalogJson : JsonSerializerContext;