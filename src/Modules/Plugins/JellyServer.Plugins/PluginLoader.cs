using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using JellyServer.Plugins.Contracts;

namespace JellyServer.Plugins;

/// <summary>A task a plugin registered, before the host wraps it.</summary>
internal sealed record PluginTaskRegistration(PluginTaskInfo Info, Type JobType);

/// <summary>A plugin that loaded and registered successfully.</summary>
internal sealed class LoadedPlugin(IServerPlugin plugin, PluginContext context, string source)
{
    public IServerPlugin Plugin { get; } = plugin;
    public PluginContext Context { get; } = context;
    public string Source { get; } = source;
}

/// <summary>What happened while looking for plugins; logged once the host has a logger.</summary>
internal sealed record PluginLoadReport(IReadOnlyList<LoadedPlugin> Loaded, IReadOnlyList<string> Failures, string Directory);

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
        if (!Directory.Exists(dir)) return new PluginLoadReport(loaded, failures, dir);

        var entries = Directory.EnumerateDirectories(dir)
            .Select(folder => Path.Combine(folder, Path.GetFileName(folder) + ".dll"))
            .Concat(Directory.EnumerateFiles(dir, "*.dll"))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var path in entries)
        {
            if (!File.Exists(path))
            {
                failures.Add($"{Path.GetFileName(Path.GetDirectoryName(path))}: expected {Path.GetFileName(path)} in the folder");
                continue;
            }
            try
            {
                var plugin = LoadOne(path, configuration, dataRoot, loaded);
                if (plugin is not null) loaded.Add(plugin);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return new PluginLoadReport(loaded, failures, dir);
    }

    private static LoadedPlugin? LoadOne(string path, IConfiguration configuration, string dataRoot, List<LoadedPlugin> already)
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
        return new LoadedPlugin(plugin, pluginContext, Path.GetFileName(path));
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
