using JellyServer.Kernel;
using JellyServer.Kernel.Data;

namespace JellyServer.Identity;

/// <summary>Users, passwords and access tokens: small and hot (every request resolves a token).</summary>
public sealed class IdentityModule : IModule
{
    public const string ModuleName = "identity";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "identity", 10, typeof(IdentityModule).Assembly,
            "JellyServer.Identity.Migrations."));
        services.AddSingleton<UserRepository>();
    }
}
