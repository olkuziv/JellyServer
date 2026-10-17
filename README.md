# JellyServer

> **A media server built from scratch in .NET 10 that speaks the Jellyfin API.**
> Point jellyfin-web, the Tizen TV app or Android TV at it, and they connect as if it were Jellyfin.

It is designed for the case where a regular media server starts to hurt: **hundreds of thousands of items, `.strm` stream collections, many users, several app instances.** It also runs on a 1 vCPU / 1 GB RAM container, with every database on a free 256 MB plan.

| | |
|---|---|
| 🔌 **Same clients** | jellyfin-web 10.11, Tizen, Android TV and other Jellyfin apps, with the startup wizard and dashboard they already know |
| 🧱 **Modular monolith** | Every module owns its data and talks to others only through contracts, enforced by architecture tests |
| 🐘 **PostgreSQL × N** | One database per module. Move any module to its own server with one connection string |
| ⚡ **Redis** | Item and query caches, a playback-progress buffer, cluster-wide locks |
| 📼 **`.strm` first** | The TV plays the stream URL directly, or the server proxies it and signs HLS playlists |
| 🎞️ **Smart playback** | Direct play → remux to HLS → transcode, with NVENC, QSV, VAAPI and software pipelines |

---

## Use case: a huge library on a tiny, nearly free server

**74,000+ movies and episodes** (15,826 movies, 2,202 series, 55,472 episodes) served from **one 1 vCPU / 1 GB RAM container**, with **nine free 256 MB PostgreSQL databases** and a 128 MB Redis. No NAS-class hardware, no single big disk, and an app container that holds no state.

| Full rescan of the whole library | Peak |
|---|---|
| CPU | **38%** of one vCPU |
| RAM | **69%** of 1 GB |
| Largest database | **57%** of 256 MB |

| Libraries | CPU during the rescan | RAM during the rescan |
|---|---|---|
| ![Libraries](docs/usecases/1%20cpu%20-%201%20gb%20ram/Libraries.png) | ![CPU](docs/usecases/1%20cpu%20-%201%20gb%20ram/cpu.png) | ![RAM](docs/usecases/1%20cpu%20-%201%20gb%20ram/ram.png) |

**Why this works:** every module has its own database, so no single one has to hold the whole library. Redis absorbs reads and buffers playback progress, scans skip every file that did not change, and the stateless container can be rebuilt or replaced at any time. Add more app instances for more users, or move a busy module to a bigger server, without changing code.

This is the **smallest** setup the design runs on. The same code scales the other way: a database server per module, several app instances behind a load balancer.

Read the full case, with the migration, storage graphs per database and a comparison with Jellyfin's single-SQLite design: [1 CPU / 1 GB RAM use case](docs/usecases/1%20cpu%20-%201%20gb%20ram/README.md).

---

## Quick start

```bash
cp .env.example .env          # optional: every value has a working default; set MEDIA_PATH and TMDB_API_KEY here
docker compose up -d --build
```

Open `http://<host>` (host port `HTTP_PORT`, default 80) or add the server in a Jellyfin app. The first start runs Jellyfin's startup wizard: pick a language, create the administrator, add libraries from `/media` and finish. Everything else is managed from the dashboard.

More: [Getting Started](docs/wiki/Getting-Started.md).

---

## Why it is fast

- **Scans that skip what has not changed.** Each item stores a fingerprint of every file that produced it. An unchanged item costs one `stat` per file. On the reference NAS, a first scan of ~79k items takes 3 minutes, and a rescan with no changes takes 39 seconds.
- **Bulk writes.** Changed items go through binary `COPY` into a temporary table, then one `INSERT … ON CONFLICT`.
- **No N+1.** A page of items needs three batched lookups: parents, user data, played counts.
- **A cache that knows when to forget.** Query results are cached in Redis under a *library generation* number. A scan bumps it only when something really changed.
- **Playback progress without write storms.** Clients report every ~10 s. The reports are buffered in Redis and flushed to PostgreSQL in one statement every few seconds, and a crash loses nothing.
- **Small images.** Artwork is shrunk once on ingest, with no full-size original stored by default. Variants are cached on disk, and duplicate requests share one resize.

Details: [Features](docs/wiki/Features.md), [Scanners & Metadata](docs/wiki/Scanners-and-Metadata.md) and [Architecture](docs/wiki/Architecture.md).

---

## Architecture at a glance

```
            ┌────────────────────────── Host (Jellyfin API) ──────────────────────────┐
            │  composition · images · streaming · transcoding glue                    │
            └───────┬───────────┬───────────┬───────────┬───────────┬─────────────────┘
                 Catalog      Media       People     UserData    Identity   … Collections · Settings · Metadata · Tasks
                    │           │           │           │           │
                  own DB      own DB      own DB      own DB      own DB      ← no foreign keys, no shared tables
```

One item is kept by three modules: **Catalog** knows what is in the library, **Media** knows what is inside the file and how to play it, **People** knows who is credited. They are put together on read and saved in an order that survives a crash half-way.

Rules, checked by `JellyServer.Architecture.Tests` on every build:

1. A module implementation references only the Kernel and `*.Contracts` projects.
2. Contracts reference only the Kernel and other contracts.
3. Only the host sees everything, and nothing references the host.
4. Namespaces equal their project names.

**Extending it is one project and one line in `Composition/AllModules.cs`:**

| To add… | Create… |
|---|---|
| a library type | `JellyServer.Scanners.<Name>` implementing `ILibraryScanner` |
| a metadata source | `JellyServer.Providers.<Name>` implementing `IMetadataProvider` |
| a hardware or software encoder | a Transcoding module implementing `IVideoPipeline` |
| a scheduled task | an `IScheduledTask` in any module |

Layout, contracts, events and databases: [Architecture](docs/wiki/Architecture.md). Step by step: [Writing a Module](docs/wiki/Writing-a-Module.md).

---

## What works today

- **Libraries:** movies, TV shows, and mixed libraries (movies and shows together), with incremental scans
- **Metadata:** NFO files, artwork next to the media, and TheMovieDb (cast, crew, seasons, episodes, images). Local data always wins
- **Versions:** several files of one movie or episode as one item with a version picker
- **Playlists and box sets:** sharing, public playlists, ordering and shuffle
- **People:** person pages with filmography and portraits
- **`.strm`:** `direct`, `redirect`, `proxy` and `auto` modes (browsers get a proxy, TVs play direct)
- **Transcoding:** direct play, HLS remux, and H.264 transcode with HDR tone-mapping, on NVENC, QSV, VAAPI or x264
- **Operations:** JSON logs, `/health`, Prometheus `/metrics`, an admin status endpoint, and a scheduled-tasks dashboard

## What does not work yet

HEVC output · embedded-subtitle extraction · music, books, photos and Live TV · automatic box sets from provider collections · metadata sources other than TMDB · "Identify". See the [Roadmap](docs/wiki/Roadmap.md).

---

## Documentation

| Page | What is in it |
|---|---|
| [Getting Started](docs/wiki/Getting-Started.md) | Docker, `.env`, the first-run wizard |
| [Configuration](docs/wiki/Configuration.md) | Every setting and its default, and how to override it with environment variables |
| [Features](docs/wiki/Features.md) | Versions, playlists and collections, people, playback progress, scheduled tasks, observability |
| [Architecture](docs/wiki/Architecture.md) | Modules, contracts, databases, events, the rules |
| [Writing a Module](docs/wiki/Writing-a-Module.md) | A step-by-step guide from an empty project to a registered module |
| [Scanners & Metadata](docs/wiki/Scanners-and-Metadata.md) | How libraries are read and how provider data is merged |
| [Playback & Transcoding](docs/wiki/Playback-and-Transcoding.md) | Decision logic, pipelines, GPU setup |
| [Deployment](docs/wiki/Deployment.md) | One database or many, pool sizes, running several instances, upgrades |
| [Roadmap](docs/wiki/Roadmap.md) | What comes next |

## Contributing

Pull requests are welcome, and the easiest ones to review are **new scanners, metadata providers and encoder pipelines**. Each one is a separate project and cannot break its neighbours. Before opening a PR:

```bash
dotnet test        # includes the architecture rules
```

If a Jellyfin client asks for an endpoint that is missing, the server logs every 404 with its query string. Paste that line into an issue and it is usually a small fix.

---

<sub>JellyServer is an independent project and is not affiliated with the Jellyfin project. "Jellyfin" is used to describe API compatibility.</sub>
