using System.Text.Json.Serialization;

namespace JellyServer.Plugins;

/// <summary>An installed plugin as <c>GET /Plugins</c> returns it (PascalCase, like Jellyfin's PluginInfo).</summary>
public sealed class PluginInfoDto
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? ConfigurationFileName { get; set; }
    public string Description { get; set; } = "";

    /// <summary>The plugin's Guid without dashes.</summary>
    public string Id { get; set; } = "";

    public bool CanUninstall { get; set; }
    public bool HasImage { get; set; }
    public PluginStatus Status { get; set; }
}

/// <summary>A plugin offered by a repository (camelCase, the repository manifest's own format).</summary>
public sealed class PackageInfoDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("overview")] public string Overview { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("guid")] public string Guid { get; set; } = "";
    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }
    [JsonPropertyName("versions")] public List<PackageVersionDto> Versions { get; set; } = [];
}

public sealed class PackageVersionDto
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    [JsonPropertyName("targetAbi")] public string? TargetAbi { get; set; }

    /// <summary>The package zip; relative addresses are resolved against the repository's address.</summary>
    [JsonPropertyName("sourceUrl")] public string? SourceUrl { get; set; }

    /// <summary>MD5 of the zip as 32 hex digits.</summary>
    [JsonPropertyName("checksum")] public string? Checksum { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("repositoryName")] public string RepositoryName { get; set; } = "";
    [JsonPropertyName("repositoryUrl")] public string RepositoryUrl { get; set; } = "";
}

/// <summary>Entry of <c>GET /web/ConfigurationPages</c>: a plugin page the dashboard links to.</summary>
public sealed class ConfigurationPageInfoDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool EnableInMainMenu { get; set; }
    public string? MenuSection { get; set; }
    public string? MenuIcon { get; set; }
    public string PluginId { get; set; } = "";
}

/// <summary>A plugin's settings as the generated settings page needs them.</summary>
public sealed class PluginSettingsDto
{
    public string Name { get; set; } = "";
    public List<PluginSettingsSectionDto> Sections { get; set; } = [];
}

public sealed class PluginSettingsSectionDto
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public List<PluginSettingFieldDto> Fields { get; set; } = [];
}

public sealed class PluginSettingFieldDto
{
    /// <summary>The full configuration key (prefix and key).</summary>
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>Text, Secret, Number, Toggle or Select.</summary>
    public string Type { get; set; } = "Text";

    public string Default { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? Options { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
    public bool RestartRequired { get; set; }

    /// <summary>What applies now (a secret is masked).</summary>
    public string Value { get; set; } = "";

    /// <summary>Changed in the dashboard (otherwise <see cref="Value"/> is the default or comes from the environment).</summary>
    public bool Overridden { get; set; }
}

/// <summary>Body of <c>POST /Plugins/{id}/Configuration</c>: the full key of each setting with its new value; null puts it back to the default.</summary>
public sealed class PluginSettingsUpdateDto
{
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class RepositoryInfoDto
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public bool Enabled { get; set; } = true;
}
