using JellyServer.Catalog.Contracts;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.People.Contracts;
using JellyServer.Tasks.Contracts;

namespace JellyServer.People;

/// <summary>
/// Cast and crew of catalog items: each person once, each item with its list of credits. Schema <c>people</c>, its own server with
/// <c>ConnectionStrings:people</c>.
/// </summary>
public sealed class PeopleModule : IModule
{
    public const string ModuleName = "people";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "people", 10, typeof(PeopleModule).Assembly, "JellyServer.People.Migrations."));
        services.AddSingleton<PeopleStore>();
        services.AddSingleton<IPeopleStore>(sp => sp.GetRequiredService<PeopleStore>());
        services.AddItemLinkedData<PeopleStore>();
        services.AddSingleton<IScheduledTask, RefreshPeopleTask>();
    }
}
