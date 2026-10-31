using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using JellyServer.Plugins.Contracts;

namespace JellyServer.Plugins;

/// <summary>A task a plugin registered, before the host wraps it.</summary>
internal sealed record PluginTaskRegistration(PluginTaskInfo Info, Type JobType);

/// <summary>A plugin that loaded and registered successfully.</summary>
internal sealed class LoadedPlugin(IServerPlugin plugin, PluginContext context, string source, string? folder, PluginMeta meta)
{
    public IServerPlugin Plugin { get; } = plugin;
    public PluginContext Context { get; } = context;
    public string Source { get; } = source;

    /// <summary>The plugin's own folder; null for a single <c>.dll</c> dropped straight into the plugins directory (those cannot be managed).</summary>
    public string? Folder { get; } = folder;
    public PluginMeta Meta { get; } = meta;
}

/// <summary>A plugin folder that was not loaded: disabled by the administrator or failed to load.</summary>
internal sealed record PluginEntry(string Folder, PluginMeta Meta, PluginStatus Status);

/// <summary>What happened while looking for plugins; logged once the host has a logger.</summary>
internal sealed record PluginLoadReport(
    IReadOnlyList<LoadedPlugin> Loaded, IReadOnlyList<string> Failures, string Directory, IReadOnlyList<PluginEntry>? Skipped = null)
{
    public IReadOnlyList<PluginEntry> NotLoaded { get; } = Skipped ?? [];
}

/// <summary>
/// Finds plugins in <c>&lt;Plugins:Directory&gt;</c> (default <c>{DataDir}/plugins</c>): every sub-folder with a
/// <c>&lt;Folder&gt;.dll</c> and every loose <c>*.dll</c>. Each plugin gets its own <see cref="AssemblyLoadContext"/>, so its
/// dependencies cannot clash with the host's or with other plugins'. A plugin that fails to load is skipped, never fatal.
/// </summary>
internal static partial class PluginLoader
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    public static string DirectoryFor(IConfiguration configuration) =>
        configuration["Plugins:Directory"] is { Length: > 0 } dir ? dir : Path.Combine(configuration["JellyServer:DataDir"] ?? "/data", "plugins");

    public static PluginLoadReport Load(IConfiguration configuration)
    {
        var dir = DirectoryFor(configuration);
        var dataRoot = configuration["Plugins:DataDirectory"] is { Length: > 0 } d ? d : Path.Combine(configuration["JellyServer:DataDir"] ?? "/data", "plugin-data");
        var loaded = new List<LoadedPlugin>();
        var failures = new List<string>();
        var skipped = new List<PluginEntry>();
        if (!Directory.Exists(dir)) return new PluginLoadReport(loaded, failures, dir);

        // Half-finished installs from a previous run.
        TryDelete(Path.Combine(dir, PluginInstaller.StagingFolder));

        var entries = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(dir).Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase))
        {
            var meta = PluginMetaFile.Read(folder);
            var name = Path.GetFileName(folder);
            if (meta?.Status == PluginStatus.Deleted)
            {
                // Removed in the dashboard: the files could not go while the plugin was loaded, so they go now.
                TryDelete(folder);
                continue;
            }
            if (meta?.Status == PluginStatus.Disabled)
            {
                skipped.Add(new PluginEntry(folder, meta, PluginStatus.Disabled));
                continue;
            }
            entries.Add(Path.Combine(folder, meta?.MainAssembly is { Length: > 0 } main ? main : name + ".dll"));
        }
        entries.AddRange(Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase));

        foreach (var path in entries)
        {
            var folderOfEntry = Path.GetDirectoryName(Path.GetFullPath(path));
            var ownFolder = string.Equals(Path.TrimEndingDirectorySeparator(folderOfEntry ?? ""), Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), StringComparison.OrdinalIgnoreCase)
                ? null : folderOfEntry;
            if (!File.Exists(path))
            {
                failures.Add($"{Path.GetFileName(Path.GetDirectoryName(path))}: expected {Path.GetFileName(path)} in the folder");
                if (ownFolder is not null) skipped.Add(Broken(ownFolder));
                continue;
            }
            try
            {
                var plugin = LoadOne(path, ownFolder, configuration, dataRoot, loaded);
                if (plugin is not null) loaded.Add(plugin);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                if (ownFolder is not null) skipped.Add(Broken(ownFolder));
            }
        }
        return new PluginLoadReport(loaded, failures, dir, skipped);
    }

    private static PluginEntry Broken(string folder)
    {
        var name = Path.GetFileName(folder);
        var meta = PluginMetaFile.Read(folder) ?? new PluginMeta { Guid = PluginMetaFile.GuidFor(name), Name = name, Version = "0.0.0.0" };
        return new PluginEntry(folder, meta, PluginStatus.Malfunctioned);
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use (Windows); the next start tries again.
        }
    }

    private static LoadedPlugin? LoadOne(string path, string? folder, IConfiguration configuration, string dataRoot, List<LoadedPlugin> already)
    {
        var contracts = typeof(IServerPlugin).Assembly.GetName();
        var context = new PluginLoadContext(path);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));

        var built = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == contracts.Name);
        if (built?.Version?.Major is { } major && major != contracts.Version!.Major)
            throw new InvalidOperationException($"built for plugin API {major}, this server provides {contracts.Version.Major}");

        var type = assembly.GetExportedTypes().FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && typeof(IServerPlugin).IsAssignableFrom(t))
            ?? throw new InvalidOperationException("no public class implements IServerPlugin");
        var plugin = (IServerPlugin)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("cannot create " + type.Name));

        var id = plugin.Id ?? "";
        if (!IdPattern().IsMatch(id))
            throw new InvalidOperationException($"invalid plugin id '{id}' (use a-z, 0-9, '.', '_', '-')");
        if (already.Any(p => p.Plugin.Id == id))
            throw new InvalidOperationException($"a plugin with id '{id}' is already loaded");

        var dataDir = Path.Combine(dataRoot, id);
        Directory.CreateDirectory(dataDir);
        var pluginContext = new PluginContext(id, dataDir, configuration.GetSection($"Plugins:Settings:{id}"));
        plugin.Register(pluginContext);

        var meta = folder is null ? null : PluginMetaFile.Read(folder);
        meta ??= new PluginMeta { Guid = PluginMetaFile.GuidFor(id), Name = plugin.Name, Version = PluginMetaFile.NormalizeVersion(plugin.Version) };
        return new LoadedPlugin(plugin, pluginContext, Path.GetFileName(path), folder, meta);
    }
}

/// <summary>Resolves a plugin's own dependencies from its folder and everything the host already has from the host.</summary>
internal sealed class PluginLoadContext(string mainPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainPath))
{
    private readonly AssemblyDependencyResolver _resolver = new(mainPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // The plugin contracts, the framework and the host's libraries must be the same types on both sides.
        if (IsSharedWithHost(name)) return null;
        return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

    private static bool IsSharedWithHost(AssemblyName name) =>
        name.Name is { } n && (Default.Assemblies.Any(a => a.GetName().Name == n)
            || File.Exists(Path.Combine(AppContext.BaseDirectory, n + ".dll"))
            || File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), n + ".dll")));
}
