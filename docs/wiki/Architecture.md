# Architecture

JellyServer is a **modular monolith**: one deployable process, split inside into modules that are isolated from each other and easy to add. The same design that runs on one 1 GB container can be spread over many databases and app instances without code changes.

## Repository layout

```
src/Kernel/JellyServer.Kernel/     IModule, per-module databases and migrations, events, Redis cache, ids
src/Modules/<Name>/
  JellyServer.<Name>.Contracts/    what other modules and the host may use: types, events, service interfaces
  JellyServer.<Name>/              the implementation: tables, migrations, repositories
src/Scanners/                      one project per library type (Movies, TvShows, Mixed) + shared file-system helpers
src/Providers/                     internet metadata sources, one project each (Tmdb)
src/Transcoding/                   IVideoPipeline contract + one project per encoder
src/JellyServer/                   the host: composition, Jellyfin API, images, streaming, transcoding glue
tests/                             unit tests + JellyServer.Architecture.Tests
```

Namespaces equal the project they live in (`JellyServer.Catalog`, `JellyServer.Scanners.Movies`, …).

## The pieces

```
                         ┌──────────────── Host: JellyServer ────────────────┐
  Jellyfin clients ────► │ Jellyfin API · images · streaming · transcoding   │
                         │ Composition/AllModules.cs                         │
                         └──┬─────────┬─────────┬─────────┬─────────┬────────┘
                            │         │         │         │         │
 Data modules:          Catalog     Media     People   UserData   Identity
                        Collections Settings  Metadata  Tasks
 Plug-ins:              Scanners (movies, tvshows, mixed) · Providers (tmdb) · Encoders (x264, nvenc, …)
                            │
                         Kernel: IModule · Database/Migrator · EventBus · RedisCache · Ids
```

| Module | Owns (schema) | Answers |
|---|---|---|
| **Identity** | `identity` | Users and authentication |
| **UserData** | `userdata` | Played, favourites, resume positions. Buffers progress in Redis |
| **Catalog** | `catalog` | What is in the library: hierarchy, names, sort and search keys, artwork, genres, provider ids, path. Runs the scan coordinator |
| **Media** | `media` | What is inside the file and how to play it: one row per file or version, streams, subtitles |
| **People** | `people` | Who is credited: each person once, plus credits per item |
| **Collections** | `collections` | Playlists and box sets |
| **Settings** | `settings` | Server and library settings edited on the dashboard |
| **Metadata** | `metadata` | Orchestrates internet providers and keeps their raw answers |
| **Tasks** | `tasks` | The scheduler behind *Scheduled Tasks* |

An item is deliberately split over three modules (Catalog, Media, People), so that the busiest data can sit on separate servers and grow apart.

## Contracts

Every module has two projects:

- **`JellyServer.<Name>.Contracts`** holds what others may use: records, service interfaces and events. It references only the Kernel and other contracts.
- **`JellyServer.<Name>`** holds the implementation (tables, SQL, services). Nothing else references it except the host.

A module talks to another only through such an interface. Examples that exist today:

| Caller | Asks | Through |
|---|---|---|
| Catalog | which items are favourite or played | `IUserDataStore` |
| Catalog | what the providers reported when a scan rewrites an item | `IRemoteMetadataSource` (implemented by Metadata) |
| Catalog filters | item ids for `PersonIds` or a box set as `ParentId` | People, Collections |
| Tasks | which jobs exist | `IScheduledTask` implementations registered by any module |
| Scan coordinator | how to read a library type | `ILibraryScanner` |

## Databases

Each data module owns a PostgreSQL **schema** and its own migrations. By default all modules share one server (`ConnectionStrings:Postgres`). A connection string named after a module (`ConnectionStrings:catalog`) moves *that module* to its own server or database with no code change.

Rules of data ownership:

- **One module, one database.** When a module's data outgrows its database, a part with a meaning of its own becomes a new module. This is how Media and People were split off Catalog.
- **Modules never read each other's tables**, and there are **no foreign keys between modules.**
- Consistency across modules is kept by *write order* and *cleanup*, not by constraints. `ItemWriter` saves files and credits first and the catalog row last, so a write that fails half-way leaves the old row and fingerprint, and the next scan writes the item again.

### Migrations

SQL files named `NNN_name.sql` are embedded in the module assembly (`Migrations/001_init.sql`). On startup the Kernel waits for every database, then applies each module's pending scripts in name order, tracked in a `schema_migrations` table of that schema. A PostgreSQL advisory lock makes replicas starting together safe: only one applies a script.

## Events

`IEventBus` is an **in-process** publish/subscribe bus. The publisher owns the event type (in its Contracts project), and subscribers react without the publisher knowing them. Handlers run one after another, and a failing handler is logged and never stops the others.

| Event | Published by | Reacted to by |
|---|---|---|
| `UserDeleted` | Identity | UserData (cleans up), Collections (hands over or removes playlists) |
| `LibraryRemoved` | Catalog | UserData and every module with item-linked data |
| `ItemsRemoved` | Catalog | Media, People, Collections via `IItemLinkedData` |

Events stay inside one process. With several app instances, a handler must be idempotent per instance, and cluster-wide work uses Redis locks.

### Item-linked data

A module that keeps data per catalog item (Media, People, Collections) implements `IItemLinkedData` and registers it with `AddItemLinkedData<T>()`. It is then told when items are deleted. A scheduled task (*Clean up collections and playlists*) removes whatever an interrupted run left behind by paging through the module's item ids.

## Caching and state

- **Redis** holds items (hydrated with one `MGET`), query pages (ids plus total count), the playback-progress buffer and cluster locks. Query cache keys include a *library generation* that a scan bumps only when something changed.
- **App containers are stateless.** Everything durable is in the databases and the `/data` volume (artwork and image cache), so a container can be rebuilt or replaced, and more can be run behind a load balancer.

## The rules

Enforced on every build by `JellyServer.Architecture.Tests`:

1. A module implementation references only the Kernel and `*.Contracts` projects, never another module's implementation.
2. `*.Contracts` reference only the Kernel and other contracts.
3. Only the host sees everything, and nothing references the host.
4. Namespaces equal their project.
5. Scanner projects and contracts contain no database code (no Npgsql, Dapper or Redis).

If a change breaks a rule, the test fails with the offending reference.

## Composition

The host lists every module in one place, [`Composition/AllModules.cs`](../../src/JellyServer/Composition/AllModules.cs):

```csharp
public static readonly IModule[] Core =
[
    new IdentityModule(), new UserDataModule(), new CatalogModule(), new MediaModule(), new PeopleModule(), new CollectionsModule(),
    new SettingsModule(), new TasksModule(),
    new MoviesScannerModule(), new TvShowsScannerModule(), new MixedScannerModule(),
    new MetadataModule(), new TmdbModule(),
];
```

A module is an `IModule`, with a name and a `Register(IServiceCollection, IConfiguration)` method that adds its services. Adding a feature means adding a project and a line in this array. See [Writing a Module](Writing-a-Module).

## Performance principles

Design decisions that follow from the target (hundreds of thousands of items, small hosts):

- **Never parse what has not changed.** Scans compare a fingerprint (name, size, mtime of every contributing file).
- **Batch everything.** Binary `COPY` for writes, `MGET` for reads, three batched lookups per page of DTOs.
- **Bound the memory.** Image compression runs one at a time by default, resize work is capped at the CPU count, and the image cache has a size limit.
- **Protect the disk.** Library folders are mounted read-only, and disposable transcoding segments go to a temp directory off the data volume.
- **Cap the database connections.** Pool sizes are split per module when they share a server (see [Deployment](Deployment)).
