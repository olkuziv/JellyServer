# JellyServer

A media server written from scratch in .NET 10 that speaks the **Jellyfin API**, so Jellyfin clients (Tizen TV app, jellyfin-web, Android TV…) connect to it as if it were Jellyfin. It is built for large libraries (100k+ items) and `.strm` stream collections.

PostgreSQL holds the data, Redis provides caching, and the whole stack runs from Docker.

## Quick start

```bash
cp .env.example .env          # optional: every value has a working default; set MEDIA_PATH and TMDB_API_KEY here
docker compose up -d --build
```

Open `http://<host>` (host port `HTTP_PORT`, default 80) or add the server in a Jellyfin app. The first start runs Jellyfin's **startup wizard**: pick the language, create the administrator, add libraries from `/media` and finish.

## Documentation

**Start at the [wiki home page](docs/wiki/Home.md).**

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
| [Roadmap](docs/wiki/Roadmap.md) | What is not implemented yet and what comes next |
| [1 CPU / 1 GB RAM](docs/usecases/1%20cpu%20-%201%20gb%20ram/README.md) | Measured on a small host, with graphs |

## At a glance

- **Modular monolith.** Every module owns its data and talks to others only through contracts. Architecture tests enforce it.
- **A database per module.** All modules share one PostgreSQL by default (one schema each). A connection string per module moves it to its own server.
- **Fast on big libraries.** Incremental scans (a rescan of ~79k unchanged items takes 39 s), binary `COPY` writes, Redis query cache, no N+1 queries.
- **`.strm` first.** The TV plays the stream directly, or the server proxies it.
- **Smart playback.** Direct play, HLS remux, or transcode on NVENC, QSV, VAAPI or software.

## Layout

```
src/Kernel/JellyServer.Kernel/     IModule, per-module databases and migrations, events, Redis cache, ids
src/Modules/<Name>/                Contracts + implementation per module (Identity, UserData, Catalog, Media, People,
                                   Collections, Settings, Metadata, Tasks)
src/Scanners/                      one project per library type (Movies, TvShows, Mixed)
src/Providers/                     internet metadata sources (Tmdb)
src/Transcoding/                   IVideoPipeline contract + one project per encoder
src/JellyServer/                   the host: composition, Jellyfin API, images, streaming
tests/                             unit tests + JellyServer.Architecture.Tests
```

Details: [Architecture](docs/wiki/Architecture.md).

## Contributing

Run `dotnet test` before opening a PR, since it includes the architecture rules. New scanners, metadata providers and encoders are separate projects and the easiest to review: see [Writing a Module](docs/wiki/Writing-a-Module.md).
