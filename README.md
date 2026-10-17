# JellyServer

A media server written from scratch in .NET 10 that speaks the **Jellyfin API**, so Jellyfin clients (Tizen TV app, jellyfin-web, Android TV…) connect to it as if it were Jellyfin. It is built for large libraries (100k+ items) and `.strm` stream collections.

PostgreSQL holds the data, Redis provides caching, and the whole stack runs from Docker.

## Quick start

```bash
cp .env.example .env          # optional: every value has a working default; set MEDIA_PATH and TMDB_API_KEY here
docker compose up -d --build
```

The media folder (`MEDIA_PATH`, default `./media`) is mounted read-only at `/media` inside the container.

Open `http://<host>` (host port `HTTP_PORT`, default 80; inside the container the server listens on 8096) or add the server in a Jellyfin app. The first start runs Jellyfin's **startup wizard**: pick the language, create the administrator, add libraries (the folder picker browses the container, so choose folders under `/media`), and finish. Libraries and users live in the database and are managed from the dashboard afterwards; nothing about them is configured through files or environment variables.

## Configuration

`.env.example` lists every variable the compose file reads (ports, media folder, TMDB key, database, artwork and logging options) with its default. More server settings come from `config/appsettings.json` (section `JellyServer`, optional). Any setting can be overridden with an environment variable, for example `JellyServer__Playback__StrmMode=proxy`.

| Setting | Default | Notes |
|---|---|---|
| `Playback.StrmMode` | `direct` | `direct`: the TV plays the `.strm` URL itself (no server bandwidth). `redirect`: the server answers 302. `proxy`: the server relays the stream, rewriting HLS playlists with signed URLs (use this when the source needs it, or for browsers blocked by CORS). `auto`: clients listed in `Playback.ProxyClients` (default `Jellyfin Web`, i.e. browsers) are proxied, every other client (TVs) plays direct. |
| `Scanner.Interval` | `06:00:00` | Periodic rescan. A rescan is also triggered by `POST /Library/Refresh` (admin). |
| `Scanner.Workers` | `16` | Parallel directory readers. Raise this for network shares. |
| `Cache.QueryTtl` / `ItemTtl` | 10 min / 24 h | Redis TTLs. |
| `TMDB_API_KEY` | – | TheMovieDb key (API key or read access token). Without it no metadata is fetched from the internet. Also `JellyServer:Tmdb:ApiKey`. |
| `Metadata.Concurrency` | `4` | Items looked up in parallel (`JellyServer:Metadata`). |
| `UserData.ProgressFlushSeconds` | `5` | How often buffered playback progress is written to PostgreSQL; `0` writes every report directly. |
| `Images.Ingest.JpegQuality` | `65` | Artwork that is downloaded (TMDB, NFO links) or uploaded in the dashboard is shrunk to a box per image type (poster 500×750, backdrop 1024×576, logo 500×250, thumb 640×360) before it is stored. |
| `Images.Ingest.KeepOriginal` | `false` | `true` stores the original bytes too. By default no full-size original is ever written, and an image that cannot be decoded is dropped. |
| `Images.Ingest.MaxConcurrency` | `1` | Images compressed at the same time (low on purpose, so a small server stays responsive). `MaxDownloadMB` (20) and `MaxPixels` (40 million) bound what is accepted. |
| `Images.CacheMaxMB` | `512` | The image cache is trimmed to this size, least recently used first. `0` = unlimited. Library artwork next to your media files is never rewritten. |
| `Transcoding.TempDir` | `{DataDir}/transcodes` | Where ffmpeg writes disposable HLS segments. Compose points it at `/tmp/transcodes`, off the persistent volume. |
| `PublicUrl` | – | Address advertised by UDP discovery. |
| `SlowRequestMs` / `SlowQueryMs` | 500 / 200 | Thresholds for slow-request and slow-query warnings in the logs. |

## How it stays fast

- **Incremental scans.** Every item stores a fingerprint of all files that produced it: the video or `.strm`, the NFO, artwork and subtitles (name, size and mtime of each). An unchanged item costs one stat per file and is never parsed or written. On the reference NAS, a first scan of about 79k items takes 3 minutes; a rescan with no changes takes 39 seconds.
- **Bulk writes.** Changed items are written through binary `COPY` into a temporary table, followed by a single `INSERT … ON CONFLICT`.
- **Query cache.** Results of queries that don't depend on the user (ID page plus total count) are cached in Redis. The cache key includes a *library generation* number, which a scan bumps only when something actually changed. Items are hydrated from Redis with one `MGET`, and anything missing is loaded with a single query.
- **No N+1 queries.** Building DTOs for a page always issues three batched lookups: parents, user data and played counts.
- **Indexes for the hot paths.** Trigram index for search (handles Cyrillic; `ґ`/`г` are normalized), GIN index on genres, and composite indexes for sorting and paging.
- **Smaller responses.** Heavy fields (`Overview`, `People`, `MediaSources`…) are only included when the client asks for them via `Fields=`. Responses are compressed with Brotli or gzip.
- **Images.** Images are resized once per 50 px size bucket and cached on disk. Concurrent requests for the same image are deduplicated, and resize work is capped at the CPU count. Image URLs carry tags derived from the content, so clients can cache them indefinitely.

## Observability

- Logs are JSON lines. Every 404 is logged with its query string, which is the main signal of a missing Jellyfin endpoint. Slow requests and slow SQL are logged as warnings, and scans report progress every 10 s plus per-library statistics.
- `GET /health`: checks every module database and Redis.
- `GET /metrics`: Prometheus text format (request counts and latency buckets, cache hit rate, scan state).
- `GET /JellyServer/Status` (admin): item counts, cache statistics, last scan results.

## Layout

```
src/Kernel/JellyServer.Kernel/     IModule, per-module databases and migrations, events, Redis cache, ids
src/Modules/<Name>/
  JellyServer.<Name>.Contracts/    what other modules and the host may use: types, events, service interfaces
  JellyServer.<Name>/              the implementation: tables, migrations, repositories (Identity, UserData, Catalog, Settings, Metadata)
src/Scanners/
  JellyServer.Scanning/            file-system helpers shared by scanners: naming, NFO, directory walker
  JellyServer.Scanners.Movies/     one project per library type (movies, TvShows, Mixed)
src/Providers/                     internet metadata sources, one project each (JellyServer.Providers.Tmdb)
src/JellyServer/                   the host: composition (Composition/AllModules.cs), Jellyfin API, images, streaming, transcoding glue
src/Transcoding/
  JellyServer.Transcoding.Abstractions/   IVideoPipeline: the contract for encoder modules
  Encoders/Software/   X264, X265, SvtAv1
  Encoders/Hardware/   Nvenc, Qsv, Vaapi, Amf, V4l2m2m, Rkmpp, VideoToolbox
tests/            unit tests, plus JellyServer.Architecture.Tests (the rules below)
```

Namespaces equal the project they live in (`JellyServer.Catalog`, `JellyServer.Scanners.Movies`, ...).

## Modules and databases

Each data module owns a PostgreSQL schema (`identity`, `userdata`, `catalog`, `settings`, `metadata`, `tasks`) and its own migrations (`src/Modules/<Name>/JellyServer.<Name>/Migrations/NNN_*.sql`, tracked per schema). Modules never read each other's tables and there are no foreign keys between them. Cross-module work goes through a public contract: for example the catalog asks `IUserDataStore` for the IDs of favorite or played items and then filters its own data with them, `UserData` cleans up after the `LibraryRemoved` and `UserDeleted` events, and the catalog asks `IRemoteMetadataSource` (implemented by `Metadata`) for what providers reported when a scan rewrites an item.

By default all modules share the server in `ConnectionStrings:Postgres`. Setting `ConnectionStrings:<module>` (`identity`, `userdata`, `catalog`, `settings`, `metadata`, `tasks`) moves that module to its own server or database without code changes; the migrations of every module run against its own connection on startup. Pool sizes are split per module when they share a server (catalog 30, userdata 20, identity 10, settings 5, metadata 5, tasks 3) and taken from the connection string otherwise (`Maximum Pool Size=...`).

Managed PostgreSQL services often cap the connections per role (for example 20 on small plans): when every module has its own database, give each connection string a small `Maximum Pool Size`. Splitting also multiplies the storage: the biggest data is `catalog.items`, and the providers' raw answers (`metadata.item_remote`, about a third of the data) live apart from it in the `metadata` module.

### Rules (enforced by `JellyServer.Architecture.Tests`)

- A module implementation references only the Kernel and `*.Contracts` projects, never another module's implementation.
- `*.Contracts` reference only the Kernel and other contracts.
- Only the host sees everything; nothing references the host.
- Namespaces equal their project.

### Scanners

A scanner is a module that turns the files of one library type into catalog items: it implements `ILibraryScanner` (in `Catalog.Contracts`), reads the file system and emits items through a `ScanContext`. It never touches a database or cache: the catalog's `ScanCoordinator` picks the scanner by library type, saves what scanners emit in batches, deletes what disappeared and invalidates caches. Adding a library type means adding a `JellyServer.Scanners.<Name>` project and listing its module in `Composition/AllModules.cs`. Scanner projects and contracts are checked for database code (no Npgsql, Dapper or Redis). Available: `movies`, `tvshows`, and `mixed` (movies and shows in one library; a folder counts as a series when it has `Season N` folders or `SxxExx` files). Other types (music, books, home videos, music videos) are rejected with 400.

Library options of the dashboard are stored in full and returned unchanged, but only the paths, the content type and the display name affect scanning for now; the remaining options (metadata downloaders, trickplay, real-time monitoring, ...) are saved and not used yet.

### Metadata from the internet

The `Metadata` module looks items up through providers (`IMetadataProvider`; today `Providers.Tmdb`) and gives what it finds to the catalog, which lays it over the item: **local data wins** (NFO values, `poster.jpg` and other artwork next to the files, an NFO title keeps the name), and the provider fills what is empty. A manual refresh with "replace all" lets the provider overwrite.

- **Which item it is:** an id from the NFO (`tmdbid`, `<uniqueid type="tmdb">`), else from the folder or file name (`[tmdbid-603]`, `{tmdb-603}`, `[imdbid-tt0133093]`, `[tvdbid-81189]`), else a search by name and year, accepted only when the title and year really match. The found id is stored with the item.
- **What it brings:** title and original title, overview, tagline, dates, rating, age rating for the library's country, genres, studios, collection, cast and crew with photos, ids; posters, backdrops and logos (language order: the library's, English, no language); for series also seasons and episodes (name, overview, date, rating, still) with one request per season.
- **Per library**, from the dashboard: preferred metadata language and country, which fetchers are on and in which order, and image limits (as in Jellyfin; backdrops default to 1).
- **When:** after every scan for new or changed items, once a day for what is still missing, and on "Refresh metadata" (`POST /Items/{id}/Refresh`). Items nobody knew are asked again after 7 days, temporary failures after 1 hour. Requests are paced (20 per second) and retried on rate limits.
- **Images** are stored as TMDB URLs; the server downloads each once, compresses it (see `Images.Ingest.*`; the original is not stored) and serves resized variants like any other image.
- **Raw answers** are kept in the module's own table (`metadata.item_remote`); the catalog gets the merged values in the item's columns, and the stored answer is laid over again after a rescan rewrites the item.
- A new source (TVDB, OMDb, ...) is a new `JellyServer.Providers.<Name>` project implementing `IMetadataProvider`, plus one line in `Composition/AllModules.cs`; the dashboard then offers it and libraries order it.
### Playback progress

Clients report a position about every 10 seconds. Reports are buffered in Redis (one hash per user) and written to PostgreSQL in one statement every few seconds (`JellyServer:UserData:ProgressFlushSeconds`, default 5; `0` writes every report straight through). Readers overlay the buffered positions, so resume lists and the item data show the latest position at once; stopping playback and marking items played write through immediately. A crash loses nothing: pending hashes are renamed atomically before a flush and replayed afterwards, and the upsert only moves `last_played_at` forward. Without Redis every report is written directly.
## Versions of one movie or episode

Files that are one movie in several versions (another resolution, voice-over, subtitles baked in) are one item with a version picker, as in Jellyfin. In a movie folder every video must be named like the folder, optionally followed by ` - something`, `_something`, `.something` or `[something]`: `Movie (2020)/Movie (2020) - 1080p.mkv` and `Movie (2020)/Movie (2020) - 720p.mkv`. The file named exactly like the folder (otherwise the highest resolution, then the first by name) is the main one. Episodes with the same season and episode number group the same way. The other files live in the main item's `versions` column; the id of a version is its `MediaSourceId`. Playback progress and the played mark belong to the main item.
## Scheduled tasks

The `Tasks` module runs everything on the dashboard's *Scheduled Tasks* page. A module registers an `IScheduledTask` (from `JellyServer.Tasks.Contracts`); triggers (startup, daily, weekly, interval, max runtime) are stored in the `tasks` schema and can be changed on the dashboard. Defaults:

| Task | Does | Default triggers |
|---|---|---|
| Scan Media Library | reads the library folders (disk) | startup, every 12 h (`JellyServer:Scanner:Interval`, `:OnStartup`) |
| Download Metadata | looks up only items without data (TMDB); shows progress; Stop drops the queue | startup, every 24 h (`JellyServer:Metadata:ReconcileInterval`) |
| Refresh All Metadata | looks every item up again | Sunday 03:00 |

Times of day are server local time (set `TZ` in the container).
## Conversion (ffmpeg)

The server picks a playback method from the DeviceProfile the client sends with PlaybackInfo:

1. **Direct play.** Used when the client supports the container and codecs, for example MKV on Tizen.
2. **Remux to HLS.** Used when the container is the problem, for example MKV on iOS. Video is copied, so CPU use is close to zero. Audio is copied when the client supports it, and converted to AAC otherwise (DTS on iOS or recent Samsung TVs).
3. **Transcode to H.264.** Used only when the client can't decode the video codec. HDR is tone-mapped to SDR and 4K is scaled down to 1080p.

The client receives a complete VOD playlist up front, so seeking works immediately. Requesting a segment far from ffmpeg's current position restarts ffmpeg at that point. ffmpeg is stopped after 60 s without requests (for example while paused), and its files are deleted after 10 min.

Codecs come from the NFO `streamdetails`. When the NFO has none, the server runs `ffprobe` once on first playback and stores the result.

Settings live under `Transcoding`: `Enabled`, `SegmentSeconds` (6), `TempDir`, `Pipelines`.

### Video pipelines

Each way of producing video is a separate module behind `IVideoPipeline` (`src/JellyServer.Transcoding.Abstractions`). A pipeline owns the whole video path (decode, filters, encode); the server core adds input, audio and the HLS muxer.

| Pipeline | Module | Notes |
|---|---|---|
| `copy` | built in | Remux. Always on, never limited. |
| `nvenc` | `JellyServer.Transcoding.Nvenc` | NVIDIA. Settings in `Transcoding:Nvenc`: `Preset` (p4), `Cq` (23), `MaxConcurrent` (3), `Gpu` (0), `MaxHeight` (1080). Decodes on the GPU when NVDEC supports the source. HDR stays on the GPU only with an ffmpeg that has `tonemap_cuda` (jellyfin-ffmpeg); with the stock ffmpeg, HDR goes to x264. |
| `qsv` | `JellyServer.Transcoding.Qsv` | Intel Quick Sync. Settings in `Transcoding:Qsv`: `Device` (/dev/dri/renderD128), `Preset` (veryfast), `GlobalQuality` (23), `MaxConcurrent` (4), `MaxHeight` (1080), `ToneMap` (true), `HwDecodeCodecs`. |
| `vaapi` | `JellyServer.Transcoding.Vaapi` | Intel or AMD. Settings in `Transcoding:Vaapi`: `Device` (/dev/dri/renderD128), `Qp` (24), `MaxConcurrent` (4), `MaxHeight` (1080), `ToneMap` (true), `HwDecode`. |
| `x264` | `JellyServer.Transcoding.X264` | Software H.264. Settings in `Transcoding:X264`: `Preset` (veryfast), `Crf` (23), `MaxConcurrent` (2). The older `H264Preset`, `H264Crf` and `MaxConcurrentEncodes` keys still work. |

The default order is nvenc, qsv, vaapi, x264. A hardware pipeline is used only if a real test encode succeeds at startup, so on a machine without that GPU it is skipped. HDR is tone-mapped on the GPU only where the probe proved it works; otherwise HDR sources go to x264.

To give the container a GPU:

```bash
# NVIDIA (Linux with NVIDIA Container Toolkit, or Docker Desktop on Windows)
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d --build
# Intel / AMD (Linux): set RENDER_GID to the host's render group id
RENDER_GID=$(getent group render | cut -d: -f3) docker compose -f docker-compose.yml -f docker-compose.vaapi.yml up -d --build
```

At startup the server checks which pipelines the installed ffmpeg supports and logs the result. `Transcoding:Pipelines` sets the order of preference, for example `["vaapi", "x264"]`. Encoders that are not listed stay off. Without the setting, every registered encoder is used. Each pipeline has its own concurrency limit; when one is full, the next suitable one takes the job. A job keeps its pipeline across seeks. If ffmpeg fails before producing a segment, the job moves to the next pipeline. A pipeline that fails on three different items in a row is skipped for 5 minutes; repeated failures on one broken file don't count. `GET /JellyServer/Status` shows the active pipelines, their load and any benching. Without ffmpeg, every client gets direct play.

## Not implemented yet

- HEVC output. The nvenc, qsv and vaapi pipelines can encode HEVC, but the playback decision still always asks for H.264.
- Extracting embedded subtitles. Sidecar `.srt`, `.vtt` and `.ass` files are supported.
- Music, books, home videos, photos, Live TV, collections and playlists.
- Metadata sources other than TheMovieDb (TVDB, OMDb, fanart.tv), manual search and "Identify".
