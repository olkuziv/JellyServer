using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JellyServer.Plugins;

/// <summary>
/// Jellyfin's plugin states, as the dashboard's plugin pages understand them. Always written as a name ("Active"): the dashboard
/// treats a falsy status (the number 0) as "not installed".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginStatus>))]
public enum PluginStatus
{
    Restart = 1,
    Active = 0,
    Disabled = -1,
    NotSupported = -2,
    Malfunctioned = -3,
    Superseded = -4,
    Deleted = -5,
}

/// <summary>
/// <c>meta.json</c> in a plugin's folder: who it is for the dashboard and what the administrator decided (enabled, disabled, removed).
/// The disk is the source of truth, so the decision survives a restart, which is when plugins load.
/// </summary>
internal sealed class PluginMeta
{
    public Guid Guid { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Overview { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>The assembly with the plugin class when it is not <c>&lt;Folder&gt;.dll</c>.</summary>
    public string? MainAssembly { get; set; }

    /// <summary>Only <see cref="PluginStatus.Active"/>, <see cref="PluginStatus.Disabled"/> and <see cref="PluginStatus.Deleted"/> are stored.</summary>
    public PluginStatus Status { get; set; } = PluginStatus.Active;
}

internal static class PluginMetaFile
{
    public const string FileName = "meta.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static PluginMeta? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginMeta>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string folder, PluginMeta meta) =>
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(meta, Json));

    /// <summary>The Guid of a plugin that came without a <c>meta.json</c> (copied in by hand): the same on every start.</summary>
    public static Guid GuidFor(string key) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("jellyserver-plugin:" + key.ToLowerInvariant())));

    /// <summary>Versions are shown and compared with four parts (<c>1.0.0.0</c>), as Jellyfin does.</summary>
    public static string NormalizeVersion(string? version)
    {
        var text = version?.Trim() ?? "";
        if (text.Length > 0 && !text.Contains('.')) text += ".0"; // "2" is a version to people, not to Version.Parse
        if (!System.Version.TryParse(text, out var v)) return "0.0.0.0";
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)).ToString();
    }
}
