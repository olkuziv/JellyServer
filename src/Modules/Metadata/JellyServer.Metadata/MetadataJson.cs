using System.Text.Json.Serialization;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Metadata;

/// <summary>Source-generated JSON for what the module stores (the providers' answers, a jsonb column).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RemoteMetadata))]
public sealed partial class MetadataJson : JsonSerializerContext;
