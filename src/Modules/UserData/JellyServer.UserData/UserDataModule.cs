using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using JellyServer.UserData.Contracts;

namespace JellyServer.UserData;

/// <summary>Per-user item state (progress, played, favorites) and display preferences: write-heavy.</summary>
public sealed class UserDataModule : IModule
{
    public const string ModuleName = "userdata";

    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(ModuleName, "userdata", 20, typeof(UserDataModule).Assembly,
            "JellyServer.UserData.Migrations."));
        services.AddSingleton(new UserDataOptions(TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("JellyServer:UserData:ProgressFlushSeconds", 5)))));
        services.AddSingleton<ProgressBuffer>();
        services.AddHostedService(sp => sp.GetRequiredService<ProgressBuffer>());
        services.AddSingleton<IUserDataStore, UserDataStore>();
        services.AddHostedService<UserDataCleanup>();
    }
}
