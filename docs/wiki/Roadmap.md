# Roadmap

Where the project stands and where it is going. The goal does not change: **a server that speaks the Jellyfin API completely, built as a modular monolith that stays fast and light on large libraries.**

Everything below the *Done* section is a plan, not a promise. Items are in rough priority order within each group.

## Done

- Modular monolith: Kernel, per-module databases and migrations, in-process events, Redis cache, architecture tests that enforce the module rules
- Catalog split into **Catalog, Media and People**, plus **Collections** (playlists and box sets)
- Incremental scanner with fingerprints, binary `COPY` writes and a library-generation query cache
- Movies, TV shows and mixed libraries; several versions of one movie or episode
- TheMovieDb metadata, with local NFO and artwork always winning
- `.strm` playback in four modes (`direct`, `redirect`, `proxy`, `auto`)
- Direct play, HLS remux and H.264 transcode with pluggable pipelines: NVENC, QSV, VAAPI, AMF, VideoToolbox, Rkmpp, V4L2 M2M, x264, x265, SVT-AV1
- Compressed artwork ingest with a bounded image cache
- Playback-progress buffering in Redis
- **Plug-in system** (`Plugins` module and `JellyServer.Plugins.Contracts` SDK): plugins are loaded from a folder at startup, get their own settings and storage, and can add library content (see [Plugins](Plugins))
- **Plugins from the dashboard**: repositories, catalog, install, enable, disable and remove on the Jellyfin *Plugins* page, plus `/System/Restart` and `/System/Shutdown`
- **Plugin settings pages**: a plugin declares its settings and the server builds the dashboard page; saved values override files and environment (see [Plugins](Plugins))
- **User policy**: the dashboard's user policy is stored and enforced (disabled users, library access, parental rating and tag restrictions applied inside the catalog queries)
- **WebSocket hub** with `ScheduledTasksInfo`, `Sessions`, `UserDataChanged`, `LibraryChanged` and `ServerShuttingDown`, so the dashboard's task page and sessions update live
- **WebP artwork**: stored and served as WebP, with JPEG/PNG for clients that do not accept it, bounded resize concurrency and a "Clean Image Cache" scheduled task
- Scheduled tasks dashboard, `/health`, `/metrics`, `/JellyServer/Status`
- The reference deployment on 1 vCPU / 1 GB RAM with nine 256 MB databases

## Next: playback

- **HEVC output.** The nvenc, qsv and vaapi pipelines can already encode HEVC, but the playback decision still always asks for H.264.
- **Embedded subtitle extraction.** Sidecar `.srt`, `.vtt` and `.ass` already work.
- A measured **Jellyfin comparison** on the same 1 vCPU / 1 GB host. For now the comparison is architectural.

## Next: metadata

- **Other sources:** TVDB, OMDb, fanart.tv. Each one is a `JellyServer.Providers.<Name>` project, so they can be added independently.
- **Manual search and "Identify"** for items the automatic match missed.
- **Automatic box sets** from the provider's collection ("Automatically add to collection" library option), and box set artwork from the provider.
- Library options that are saved but not yet used: metadata downloaders per type, real-time monitoring (trickplay and chapter images are under *Next: client parity*).

## Next: client parity

Things the Jellyfin web client shows or calls that the server does not do yet. The library options below are already stored and returned, but nothing acts on them.

- **Trickplay.** Generating the preview tiles with ffmpeg (a background task with progress, on demand and during a library scan, optionally next to the media), and serving them (`/Videos/{id}/Trickplay/{width}/{index}.jpg` plus `Trickplay` in `BaseItemDto`).
- **Chapter images.** Extracting a frame per chapter and serving `/Items/{id}/Images/Chapter/{index}`.
- **Save images and metadata next to the media** (`SaveLocalMetadata`): writing artwork and NFO files back to the library folders.
- **Remote image search** ("Edit images" → search the internet): `RemoteImages` and downloading the chosen one. TMDB only supplies the primary image, backdrops and logo; thumb, banner, art and disc need fanart.tv or TVDB.
- **Cover art embedded in video files** (for example MKV attachments), read with ffmpeg.
- **WebSocket topics.** The hub is in place (`ScheduledTasksInfo`, `Sessions`, `UserDataChanged`, `LibraryChanged`, `ServerShuttingDown`). Missing, each needing its own backend first:
  - `RefreshProgress` (progress per item; today only the whole metadata queue is known)
  - remote control: `Play`, `Playstate`, `GeneralCommand` and the `/Sessions/{id}/Playing`, `/Command`, `/Message` endpoints
  - `ActivityLogEntry` and a real activity log (`/System/ActivityLog/Entries` is an empty stub)
  - SyncPlay
  - Live TV timers (`TimerCreated`, `SeriesTimerCreated`, ...)
- **Plugin image.** `HasImage` is always false and `/Plugins/{id}/{version}/Image` is a 404, so installed plugins show no picture (the catalog still shows the repository's `imageUrl`). Needs an image in the package.
- **Several replicas.** Task state and socket notifications are per process; a client on another replica does not see a task that runs elsewhere. Needs Redis pub/sub.

## Next: library types

Each of these is a new scanner project plus, where needed, a data module:

- **Music** (and InstantMix)
- **Books**
- **Home videos and photos**
- **Music videos**
- **Live TV**

## Scale and operations

- A measured look at the next database to fill up (`metadata`, at 65% on the reference host), and a plan to trim or split the providers' raw answers into a module of their own
- Cluster-wide events (today they are in-process, so handlers are idempotent per instance)

## How to help

The easiest contributions to review are the ones that fit the plug-in points, since each one is a separate project and cannot break its neighbours:

| You could add | As |
|---|---|
| A metadata source | `JellyServer.Providers.<Name>` implementing `IMetadataProvider` |
| A library type | `JellyServer.Scanners.<Name>` implementing `ILibraryScanner` |
| An encoder | `JellyServer.Transcoding.<Name>` implementing `IVideoPipeline` |
| A new feature module | Two projects following [Writing a Module](Writing-a-Module) |

Missing endpoints are the most common bug: the server logs every 404 with its query string. If a Jellyfin client misbehaves, open an issue with that log line.

## Principles that guide the order

1. **Full Jellyfin parity over partial features.** A feature is added when it works the way Jellyfin clients expect, not as an unsupported subset.
2. **Isolation before speed.** A new capability becomes a module with a contract, not code in the host.
3. **Measure first.** Changes to storage and caching come with numbers from a real library.
4. **Small hosts count.** What does not fit 1 vCPU / 1 GB RAM and 256 MB databases needs a good reason.
