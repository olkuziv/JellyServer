using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JellyServer.Kernel;

/// <summary>
/// A self-contained part of the server. A module owns its tables (and its own database connection, see
/// <see cref="Data.ModuleDatabaseSpec"/>), registers its services here and is reachable from other
/// modules only through the public contracts it exposes.
/// </summary>
public interface IModule
{
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);
}

public static class ModuleExtensions
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration, params IModule[] modules)
    {
        services.TryAddSingleton<Events.IEventBus, Events.EventBus>();
        foreach (var module in modules) module.Register(services, configuration);
        return services;
    }
}
