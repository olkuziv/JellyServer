# Deployment

From a single `docker compose up` on a home server to separate databases and several app instances.

## Topologies

| Scale | Layout |
|---|---|
| **Home server** | The bundled compose file: one app, one PostgreSQL (nine schemas), one Redis |
| **Small cloud host** | One 1 vCPU / 1 GB app container, managed Redis, and **a small managed database per module** (the [reference deployment](../usecases/1%20cpu%20-%201%20gb%20ram/README.md)) |
| **Large library or many users** | Several stateless app containers behind a load balancer, with busy modules (catalog, media, people, userdata) on servers of their own |

The application image is the same in all of them. Only configuration changes.

## One database (default)

All modules share `ConnectionStrings:Postgres`. Each gets its own **schema** (`identity`, `userdata`, `catalog`, `media`, `people`, `collections`, `settings`, `metadata`, `tasks`) and its own migrations.

```yaml
ConnectionStrings__Postgres: "Host=postgres;Username=jelly;Password=…;Database=jelly;No Reset On Close=true"
ConnectionStrings__Redis: "redis:6379,abortConnect=false"
```

Pool sizes are **split between modules** so that together they stay within the server's connection limit:

| Module | Pool |
|---|---|
| catalog | 30 |
| userdata | 20 |
| media | 15 |
| identity | 10 |
| people | 10 |
| settings | 5 |
| metadata | 5 |
| collections | 5 |
| tasks | 3 |

That is 103 connections per app instance. The bundled PostgreSQL is configured with `max_connections=200`, so one instance fits. For several instances, raise the limit or put a pooler in front of PostgreSQL.

## A database per module

Set `ConnectionStrings:<module>` to move **that module** to its own server or database. No code changes, and migrations of each module run against its own connection on startup.

```yaml
environment:
  ConnectionStrings__Postgres: "Host=pg-main;Username=jelly;Password=…;Database=jelly"
  ConnectionStrings__catalog:  "Host=pg-catalog;Username=jelly;Password=…;Database=catalog;Maximum Pool Size=30"
  ConnectionStrings__media:    "Host=pg-media;Username=jelly;Password=…;Database=media;Maximum Pool Size=15"
  ConnectionStrings__people:   "Host=pg-people;Username=jelly;Password=…;Database=people;Maximum Pool Size=10"
```

Modules without their own string keep sharing `ConnectionStrings:Postgres`. A dedicated string **keeps its own pool size**; if it has no `Maximum Pool Size`, the module's default from the table above is used.

### Which modules to move first

They grow differently. Measured on a library of 114k items (before: one `items` table of 225 MB):

| Module | Size | Grows with |
|---|---|---|
| catalog | 121 MB (25 MB indexes) | Items |
| media | 63 MB | Files and versions (140k files) |
| people | 83 MB | People and credits (182k people) |
| metadata | the largest after catalog | The providers' raw answers, including cast lists |
| userdata | small, write-heavy | Users × played items; progress writes |

Split by **module**, not by table. If a module outgrows a database, part of its data with a meaning of its own should become a new module (this is how Media and People were taken out of Catalog).

### Managed PostgreSQL with connection caps

Managed services often cap connections per role (for example 20 on small plans). When every module has its own database, give each connection string a small `Maximum Pool Size`:

```
...;Maximum Pool Size=5
```

### Small databases

On 256 MB databases:

- Watch the usage per module. The reference deployment keeps every database below 60%.
- **In-place rewrites need as much free space again.** Dropping columns frees nothing until the table is rewritten. When a database is near its limit, the only migration that fits is `TRUNCATE` plus a rescan, and upgrade notes say when that is needed.
- Dead row versions from repeated updates are reused by autovacuum, so usage settles instead of growing without bound.

## Redis

Redis holds the item and query caches, the playback-progress buffer and cluster locks. It is a **cache**: losing it loses no data.

- The bundled Redis runs with `--maxmemory 512mb --maxmemory-policy allkeys-lru --save ""`. No persistence.
- The reference deployment uses 128 MB.
- Playback-progress hashes are renamed atomically before each flush and replayed afterwards, so a crash does not lose progress.
- Without Redis the server still runs, and every progress report is written directly to PostgreSQL.

## Running several instances

The app container is **stateless**: items, users, progress and tasks are in PostgreSQL and Redis.

What is shared and how:

| Concern | Mechanism |
|---|---|
| Migrations | A PostgreSQL advisory lock per schema: only one instance applies a script |
| Scans and clean-ups | Coordinated with Redis locks, so a scan does not run twice |
| Caches | Shared in Redis, with the library generation as part of the key |
| Playback progress | Buffered in Redis per user, flushed by whichever instance gets there |
| Events | **In-process only.** Handlers must be idempotent per instance |

What you still need to handle:

- **A shared `/data` volume** (or a replica-local one): it holds uploaded and downloaded artwork and the image cache. Image variants are cheap to regenerate, but uploaded artwork must be shared.
- **The media folder** mounted read-only on every instance, at the same path.
- **A load balancer** in front of the instances. Test WebSocket and long-running stream requests through it.
- **Transcoding load.** Each instance transcodes locally. Size `MaxConcurrent` per instance, or give GPU hosts the transcoding traffic.

## Reverse proxy and TLS

Put a reverse proxy in front for TLS. Forward WebSockets and the original host. Set `PUBLIC_URL` to the external address so discovery advertises the right one.

## Hardening checklist

- [ ] Change `DB_POSTGRES_PASSWORD` from the default.
- [ ] Do not publish the PostgreSQL and Redis ports.
- [ ] Mount media read-only (the default).
- [ ] Put the server behind TLS.
- [ ] Set `TZ` so daily and weekly tasks run at the expected local time.
- [ ] Keep `TMDB_API_KEY` in `.env`, never in the repository.

## Monitoring

| Signal | Where |
|---|---|
| Liveness and dependencies | `GET /health`, which checks every module database and Redis |
| Metrics | `GET /metrics` in Prometheus format: request counts and latency buckets, cache hit rate, scan state |
| Admin status | `GET /JellyServer/Status`: item counts, cache statistics, last scan, transcoding load |
| Logs | JSON lines. Every 404 is logged with its query string. Slow requests and slow SQL are warnings (`SlowRequestMs` 500, `SlowQueryMs` 200). Scans report progress every 10 s |
| Storage | Your database host's per-database usage. Watch `metadata` and `catalog` first |

## Backups

- **PostgreSQL** holds users, watched state, favourites, playlists and settings. Back up all module databases (`pg_dump`, or snapshots of the volume).
- **`/data`** holds artwork. It can be regenerated except for images you uploaded by hand.
- **Redis** needs no backup.
- **Catalog, Media and People** can be rebuilt from your files with a rescan. User data cannot, so back it up.

## Upgrades

```bash
git pull
docker compose up -d --build
```

Migrations run on startup, per module. When an upgrade reshapes a module (for example splitting the catalog), the release notes say whether libraries will look empty until the startup scan rewrites them. Watched state and favourites are kept, because item ids are derived from file paths. ffprobe results are re-probed on first playback.

### Upgrading to the Catalog / Media / People split (catalog migration 007)

The catalog's `items` table is **emptied** and its media and people columns dropped. Dropping columns frees no space until the table is rewritten, and a rewrite needs as much free space again, which a 256 MB database does not have.

The scan at startup then writes every item back with its files and people (into the new modules' databases) and lays the stored provider metadata over it again. Ids come from paths, so watched state and favourites still match.

- Until that scan finishes the libraries look empty (about six minutes for ~73k items on the reference host).
- **Lost on the way:** ffprobe results (probed again on first playback) and artwork uploaded for single items on the dashboard (upload it again).
