# Writing a Module

A step-by-step guide from an empty folder to a registered module. The running example is a small **Bookmarks** module (users bookmark catalog items). The real [Collections module](../../src/Modules/Collections) is built the same way and is the best reference to read alongside this page.

Read [Architecture](Architecture) first for the rules a module must follow.

## What kind of module?

| You want to add… | It is a… | Skip to |
|---|---|---|
| A feature with its own data | **Data module** | Step 1 |
| A library type (music, books…) | **Scanner module** | [Scanner modules](#scanner-modules) |
| Another internet metadata source | **Provider module** | [Provider modules](#provider-modules) |
| An encoder | **Encoder module** | [Playback & Transcoding](Playback-and-Transcoding#writing-an-encoder) |
| A job on the *Scheduled Tasks* page | **A task** inside any module | [Scheduled tasks](#scheduled-tasks) |

## Step 1: Create the two projects

```
src/Modules/Bookmarks/
  JellyServer.Bookmarks.Contracts/JellyServer.Bookmarks.Contracts.csproj
  JellyServer.Bookmarks/JellyServer.Bookmarks.csproj
```

Both are `net10.0` with `Nullable` and `ImplicitUsings` enabled. The `RootNamespace` **must equal the project name**: the architecture tests check it.

**Contracts** reference the Kernel (and the contracts of modules whose types they expose):

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\Kernel\JellyServer.Kernel\JellyServer.Kernel.csproj" />
  <ProjectReference Include="..\..\Catalog\JellyServer.Catalog.Contracts\JellyServer.Catalog.Contracts.csproj" />
</ItemGroup>
```

**Implementation** references the Kernel, its own contracts, and *contracts only* of other modules. It also embeds the migrations:

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
  <ProjectReference Include="..\..\..\Kernel\JellyServer.Kernel\JellyServer.Kernel.csproj" />
  <ProjectReference Include="..\JellyServer.Bookmarks.Contracts\JellyServer.Bookmarks.Contracts.csproj" />
  <ProjectReference Include="..\..\Catalog\JellyServer.Catalog.Contracts\JellyServer.Catalog.Contracts.csproj" />
</ItemGroup>
<ItemGroup>
  <EmbeddedResource Include="Migrations\*.sql" />
</ItemGroup>
```

Add both projects to the solution.

> A reference to another module's implementation project fails the architecture test. If you need something from it, ask for a contract.

## Step 2: Define the contract

Put in `JellyServer.Bookmarks.Contracts` only what others need: models, a service interface and events.

```csharp
namespace JellyServer.Bookmarks.Contracts;

public sealed record Bookmark(Guid UserId, Guid ItemId, DateTime CreatedUtc);

public interface IBookmarkService
{
    Task AddAsync(Guid userId, Guid itemId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid userId, CancellationToken ct);
}
```

If other modules should react to something you do, add an event record here, and publish it through `IEventBus`.

## Step 3: Write the first migration

`JellyServer.Bookmarks/Migrations/001_init.sql`:

```sql
CREATE TABLE bookmarks (
    user_id     uuid        NOT NULL,
    item_id     uuid        NOT NULL,   -- a catalog item id: no foreign key, it lives in another module
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, item_id)
);
CREATE INDEX bookmarks_item ON bookmarks (item_id);
```

Rules:

- Name files `NNN_description.sql`. They run in name order, once, and are recorded in the schema's `schema_migrations` table.
- **Never edit a migration that has shipped.** Add the next number.
- No foreign keys to other modules' tables, and no reads from them. Store the id and ask the owning module through its contract.
- Write the SQL for the module's own schema. The connection's search path is already set to it.

## Step 4: Implement the store and the service

Ask for your module's database by key. The Kernel registers one `Database` per module name:

```csharp
public sealed class BookmarkStore([FromKeyedServices(BookmarksModule.ModuleName)] Database db)
{
    public async Task AddAsync(Guid userId, Guid itemId, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO bookmarks (user_id, item_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = itemId });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

Guidelines that keep the server fast:

- Use **parameters** and Npgsql or Dapper directly. Batch writes (`COPY` or multi-row statements) instead of one statement per item.
- Hot paths should do **one query per page**, not one per row.
- `db.Timed(sql, ...)` logs queries slower than `SlowQueryMs`.
- Keep a pool in mind. Your module gets only its share of connections (step 5).

## Step 5: Write the module class

```csharp
namespace JellyServer.Bookmarks;

public sealed class BookmarksModule : IModule
{
    public const string ModuleName = "bookmarks";
    public string Name => ModuleName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDatabase(new ModuleDatabaseSpec(
            Module: ModuleName,
            Schema: "bookmarks",
            PoolSize: 5,                                   // default share when the server is shared
            Assembly: typeof(BookmarksModule).Assembly,
            MigrationsPrefix: "JellyServer.Bookmarks.Migrations."));

        services.AddSingleton<BookmarkStore>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
    }
}
```

`AddModuleDatabase` gives the module:

- its own **schema**;
- a connection from `ConnectionStrings:bookmarks` if present, otherwise the shared `ConnectionStrings:Postgres`;
- automatic migration on startup.

Nothing else is needed to make the module movable to its own server.

### Keep the data tidy when items disappear

If your table holds catalog item ids, implement `IItemLinkedData` (from Catalog contracts) so the catalog can tell you when items are deleted:

```csharp
public sealed class BookmarkStore(...) : IItemLinkedData
{
    public string Name => "bookmarks";
    public Task<IReadOnlyList<Guid>> PageItemIdsAsync(Guid? afterId, int limit, CancellationToken ct) { /* ids after afterId, in id order */ }
    public Task RemoveAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)  { /* DELETE ... WHERE item_id = ANY($1) */ }
}

// in Register:
services.AddItemLinkedData<BookmarkStore>();   // the store must already be registered as a singleton
```

You then receive `ItemsRemoved` and `LibraryRemoved` cleanup automatically, and the daily cleanup task finds leftovers from interrupted runs by paging through your ids.

### React to events

```csharp
public sealed class BookmarksUserCleanup(BookmarkStore store, IEventBus events) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = events.Subscribe<UserDeleted>((e, c) => store.RemoveUserAsync(e.UserId, c));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _subscription?.Dispose(); return Task.CompletedTask; }
}

// in Register:
services.AddHostedService<BookmarksUserCleanup>();
```

Events are in-process. Make the handler idempotent.

## Step 6: Register it in the host

Add a project reference from `src/JellyServer/JellyServer.csproj` to the implementation project, then one entry in [`Composition/AllModules.cs`](../../src/JellyServer/Composition/AllModules.cs):

```csharp
new BookmarksModule(),
```

The host is the only project allowed to see every module.

## Step 7: Expose it through the API

API endpoints live in the host (`src/JellyServer/Api`). The host injects your **contract** (`IBookmarkService`) and maps the Jellyfin-compatible routes. Modules do not define HTTP routes themselves.

If the feature exists in Jellyfin, follow Jellyfin's route and JSON shape exactly: the clients depend on it.

## Step 8: Test

```bash
dotnet test
```

- `JellyServer.Architecture.Tests` checks the reference rules and namespaces. A new module must pass without changes to the tests.
- Add unit tests next to the existing ones in `tests/`.

## Checklist

- [ ] Two projects, namespaces equal project names
- [ ] Contracts reference only the Kernel and other contracts
- [ ] Implementation references no other module's implementation
- [ ] `001_init.sql` embedded, no foreign keys to other modules
- [ ] `AddModuleDatabase` with a unique module name and schema
- [ ] Per-item data implements `IItemLinkedData`
- [ ] Listed in `AllModules.cs`
- [ ] `dotnet test` is green

---

## Scanner modules

A scanner turns the files of one library type into catalog items. It implements `ILibraryScanner` (from Catalog contracts), reads the file system and emits items through the `ScanContext`. **It never touches a database or cache.**

```csharp
public sealed class MovieScanner : ILibraryScanner
{
    public IReadOnlyCollection<string> CollectionTypes { get; } = [LibraryTypes.Movies];

    public async Task ScanRootAsync(ScanContext context, string root, CancellationToken ct)
    {
        if (context.OpenRoot(root) is not { } listing) return;
        // walk folders in parallel; emit an Item for each movie that changed
        await ParallelWalker.WalkAsync(listing.SubDirs(), context.Workers, dir => /* scan dir */, ct);
    }
}

public sealed class MoviesScannerModule : IModule
{
    public string Name => "scanner-movies";
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<ILibraryScanner, MovieScanner>();
}
```

Create `JellyServer.Scanners.<Name>`, reference `JellyServer.Scanning` (naming, NFO, directory walker) and the Catalog contracts, and add the module to `AllModules.cs`. The catalog's `ScanCoordinator` picks the scanner by library type, saves emitted items in batches, deletes what disappeared and invalidates caches. See [Scanners & Metadata](Scanners-and-Metadata).

## Provider modules

A metadata source implements `IMetadataProvider` (from Metadata contracts):

```csharp
public sealed class TmdbModule : IModule
{
    public string Name => "provider-tmdb";
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TmdbOptions>(configuration.GetSection(TmdbOptions.Section));
        services.AddSingleton<IMetadataProvider, TmdbMetadataProvider>();
    }
}
```

The provider reports a `Name`, whether it `IsAvailable` (no API key means it is ignored and not offered in the dashboard), the item and image types it supplies, and the `FetchAsync` / `FetchSeasonAsync` methods. Create `JellyServer.Providers.<Name>`, and add one line to `AllModules.cs`. The dashboard then offers it and libraries can order it.

## Scheduled tasks

Any module can add a job to *Dashboard → Scheduled Tasks* by registering an `IScheduledTask` (from `JellyServer.Tasks.Contracts`):

```csharp
services.AddSingleton<IScheduledTask, CleanBookmarksTask>();
```

```csharp
public sealed class CleanBookmarksTask : IScheduledTask
{
    public string Id => "…32 hex digits…";       // stable: clients use it in URLs
    public string Key => "CleanBookmarks";
    public string Name => "Clean up bookmarks";
    public string Description => "Removes bookmarks of items that no longer exist.";
    public string Category => "Library";
    public IReadOnlyList<TaskTrigger> DefaultTriggers => [TaskTrigger.Daily(TimeSpan.FromHours(4))];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) { /* report 0-100 */ return Task.CompletedTask; }
}
```

Triggers (`Startup`, `Interval`, `Daily`, `Weekly`) are stored in the `tasks` schema and an administrator can change them. Times of day are server local time.
