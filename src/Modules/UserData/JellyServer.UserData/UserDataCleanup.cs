using Dapper;
using JellyServer.Kernel.Data;
using JellyServer.Kernel.Events;
using JellyServer.Identity;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;

namespace JellyServer.UserData;

/// <summary>There are no foreign keys between modules, so user state of deleted items is removed by reacting to the catalog's events.</summary>
public sealed class UserDataCleanup([FromKeyedServices(UserDataModule.ModuleName)] Database db, ProgressBuffer buffer, IEventBus events, ILogger<UserDataCleanup> log)
    : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = new CompositeDisposable(events.Subscribe<LibraryRemoved>(OnLibraryRemoved), events.Subscribe<UserDeleted>(OnUserDeleted));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task OnUserDeleted(UserDeleted evt, CancellationToken ct)
    {
        await buffer.ForgetUserAsync(evt.UserId);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var removed = await conn.ExecuteAsync(new CommandDefinition("DELETE FROM user_data WHERE user_id = @id", new { id = evt.UserId }, cancellationToken: ct));
        removed += await conn.ExecuteAsync(new CommandDefinition("DELETE FROM display_preferences WHERE user_id = @id", new { id = evt.UserId }, cancellationToken: ct));
        log.LogInformation("Removed {Count} user data rows of deleted user {UserId}", removed, evt.UserId);
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    private async Task OnLibraryRemoved(LibraryRemoved evt, CancellationToken ct)
    {
        var removed = 0;
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var chunk in evt.ItemIds.Chunk(10_000))
            removed += await conn.ExecuteAsync(new CommandDefinition("DELETE FROM user_data WHERE item_id = ANY(@ids)", new { ids = chunk }, cancellationToken: ct));
        if (removed > 0) log.LogInformation("Removed {Count} user data rows of deleted library items", removed);
    }
}
