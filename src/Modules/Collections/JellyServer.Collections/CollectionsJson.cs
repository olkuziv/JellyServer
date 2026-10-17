using System.Text.Json.Serialization;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;

namespace JellyServer.Collections;

/// <summary>Source-generated JSON for the jsonb columns.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<CollectionShare>))]
[JsonSerializable(typeof(ItemImages))]
public sealed partial class CollectionsJson : JsonSerializerContext;
