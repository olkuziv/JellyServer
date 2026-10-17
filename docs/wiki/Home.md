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

## Try it in two minutes

```bash
git clone <this repository>
cd JellyServer
cp .env.example .env          # optional: set MEDIA_PATH and TMDB_API_KEY
docker compose up -d --build
```

Open `http://<host>`. The first start runs Jellyfin's startup wizard: pick a language, create the administrator, add libraries from `/media` and finish. Everything else is managed from the dashboard.

---

## Why it is fast

- **Scans that skip what has not changed.** Each item stores a fingerprint of every file that produced it. An unchanged item costs one `stat` per file. On the reference NAS, a first scan of ~79k items takes 3 minutes, and a rescan with no changes takes 39 seconds.
- **Bulk writes.** Changed items go through binary `COPY` into a temporary table, then one `INSERT … ON CONFLICT`.
- **No N+1.** A page of items needs three batched lookups: parents, user data, played counts.
- **A cache that knows when to forget.** Query results are cached in Redis under a *library generation* number. A scan bumps it only when something really changed.
- **Playback progress without write storms.** Clients report every ~10 s. The reports are buffered in Redis and flushed to PostgreSQL in one statement every few seconds, and a crash loses nothing.
- **Small images.** Artwork is shrunk once on ingest, with no full-size original stored by default. Variants are cached on disk, and duplicate requests share one resize.

Details: [Features](Features), [Scanners & Metadata](Scanners-and-Metadata) and [Architecture](Architecture).

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

Four rules, checked by `JellyServer.Architecture.Tests` on every build:

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

---

## Real numbers

The production stack runs on one `1 vCPU / 1 GB RAM` container plus **nine free 256 MB PostgreSQL databases** and a 128 MB Redis. It holds **15,826 movies, 2,202 series and 55,472 episodes**.

| Full rescan of the whole library | Peak |
|---|---|
| CPU | **38%** of one vCPU |
| RAM | **69%** of 1 GB |
| Largest database | **57%** of 256 MB |

The story of how the catalog was split into three modules to get out of the red zone (90% → 57%), with graphs: [1 CPU / 1 GB RAM use case](../usecases/1%20cpu%20-%201%20gb%20ram/README.md).

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

HEVC output · embedded-subtitle extraction · music, books, photos and Live TV · automatic box sets from provider collections · metadata sources other than TMDB · "Identify".

---

## Wiki map

| Page | What is in it |
|---|---|
| **Getting Started** | Docker, `.env`, the first-run wizard |
| **Configuration** | Every setting and its default, and how to override it with environment variables |
| **Features** | Versions, playlists and collections, people, playback progress, scheduled tasks, observability |
| **Architecture** | Modules, contracts, databases, events, the rules |
| **Writing a Module** | A step-by-step guide from an empty project to a registered module |
| **Scanners & Metadata** | How libraries are read and how provider data is merged |
| **Playback & Transcoding** | Decision logic, pipelines, GPU setup |
| **Deployment** | One database or many, pool sizes, running several instances |
| **Roadmap** | What comes next |

## Contributing

Pull requests are welcome, and the easiest ones to review are **new scanners, metadata providers and encoder pipelines**. Each one is a separate project and cannot break its neighbours. Before opening a PR:

```bash
dotnet test        # includes the architecture rules
```

If a Jellyfin client asks for an endpoint that is missing, the server logs every 404 with its query string. Paste that line into an issue and it is usually a small fix.

---

<sub>JellyServer is an independent project and is not affiliated with the Jellyfin project. "Jellyfin" is used to describe API compatibility.</sub>
