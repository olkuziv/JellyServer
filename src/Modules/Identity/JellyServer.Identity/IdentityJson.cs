using System.Text.Json.Serialization;
using JellyServer.Identity.Contracts;

namespace JellyServer.Identity;

/// <summary>Source-generated JSON for what the module caches in Redis (tokens and users).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(AuthSession))]
public sealed partial class IdentityJson : JsonSerializerContext;