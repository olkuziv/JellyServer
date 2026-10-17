using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.Tasks.Contracts;

namespace JellyServer.Tasks;

/// <summary>The scheduler behind the dashboard's Scheduled Tasks: runs the <see cref="IScheduledTask"/>s other modules register.</summary>
public sealed class TasksModule : IModule
{
    public const string ModuleName = "tasks";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "tasks", 3, typeof(TasksModule).Assembly, "JellyServer.Tasks.Migrations."));
        services.AddSingleton<TaskStore>();
        services.AddSingleton<TaskManager>();
        services.AddSingleton<ITaskManager>(sp => sp.GetRequiredService<TaskManager>());
        services.AddHostedService<TriggerRunner>();
    }
}