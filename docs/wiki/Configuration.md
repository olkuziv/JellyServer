# Configuration

Settings come from four places. Later ones win.

1. Built-in defaults
2. `config/appsettings.json`, section `JellyServer` (optional; mounted read-only at `/app/config`)
3. Environment variables
4. The dashboard, for everything about libraries, users, metadata and transcoding

## Overriding with environment variables

Any setting can be set from the environment. Use `__` (double underscore) for each level of nesting:

```yaml
JellyServer__Playback__StrmMode: proxy
JellyServer__Images__Ingest__JpegQuality: 70
JellyServer__Transcoding__Nvenc__MaxConcurrent: 2
ConnectionStrings__catalog: "Host=...;Database=..."
```

Equivalent `config/appsettings.json`:

```json
{
  "JellyServer": {
    "Playback": { "StrmMode": "proxy" },
    "Images": { "Ingest": { "JpegQuality": 70 } }
  }
}
```

`.env.example` lists every variable the compose file reads. Copy it to `.env`. The file is ignored by git, so keep secrets there.

## `.env` variables

| Variable | Default | Maps to |
|---|---|---|
| `MEDIA_PATH` | `./media` | Host folder mounted read-only at `/media` |
| `SERVER_NAME` | `JellyServer` | `JellyServer:Name` |
| `HTTP_PORT` | `80` | Host port forwarded to 8096 |
| `PUBLIC_URL` | `http://localhost` | `JellyServer:PublicUrl` |
| `TZ` | `UTC` | Time zone of scheduled tasks |
| `STRM_MODE` | `auto` | `JellyServer:Playback:StrmMode` |
| `TMDB_API_KEY` | empty | TheMovieDb key or read access token |
| `IMAGE_WEBP` | `true` | `Images:Ingest:Webp` |
| `IMAGE_WEBP_QUALITY` | `70` | `Images:Ingest:WebpQuality` |
| `IMAGE_VARIANT_QUALITY` | `80` | `Images:WebpQuality` |
| `IMAGE_JPEG_QUALITY` | `65` | `Images:Ingest:JpegQuality` (used with `IMAGE_WEBP=false`) |
| `IMAGE_KEEP_ORIGINAL` | `false` | `Images:Ingest:KeepOriginal` |
| `IMAGE_CACHE_MAX_MB` | `512` | `Images:CacheMaxMB` |
| `TRANSCODE_TEMP_DIR` | `/tmp/transcodes` | `Transcoding:TempDir` |
| `DB_POSTGRES_HOST` / `_USERNAME` / `_PASSWORD` / `_DATABASE` | `postgres` / `jelly` / `jelly` / `jelly` | `ConnectionStrings:Postgres`. The bundled postgres container reads the same values |
| `REDIS_REDIS_HOST` | `redis` | `ConnectionStrings:Redis` |
| `REQUEST_LOG_LEVEL` | `Debug` | Log level of the request logger. `Information` quiets it down |

**Change the default database password** (`jelly`/`jelly`) before exposing the server beyond your own machine.

## Server (`JellyServer:*`)

| Setting | Default | Notes |
|---|---|---|
| `Name` | `JellyServer` | Server name shown to clients |
| `Port` | `8096` | Port inside the container |
| `DataDir` | `/data` | Persistent data (the `jelly-data` volume) |
| `Version` | `10.11.0` | Version reported to clients. jellyfin-web rejects servers below its minimum, so leave it |
| `PublicUrl` | – | Address advertised by UDP discovery |
| `Discovery` | `true` | Answer UDP auto-discovery on port 7359 |
| `WebDir` | `web` | Folder with the jellyfin-web build served at `/web` |
| `SlowRequestMs` | `500` | Requests slower than this are logged as warnings |
| `SlowQueryMs` | `200` | Same for SQL |

## Playback (`JellyServer:Playback:*`)

| Setting | Default | Notes |
|---|---|---|
| `StrmMode` | `direct` in code, `auto` in compose | `direct`, `redirect`, `proxy` or `auto`. See [Playback & Transcoding](Playback-and-Transcoding#strm-files) |
| `ProxyClients` | `["Jellyfin Web"]` | In `auto` mode these clients are proxied and all others play direct |
| `PlayedThreshold` | `0.9` | Stopping past this fraction of the runtime marks the item played |
| `MinResumeFraction` | `0.05` | Positions below this fraction are not kept for resume |

## Library scanning (`JellyServer:Scanner:*`)

| Setting | Default | Notes |
|---|---|---|
| `Interval` | `12:00:00` | Periodic rescan. Also editable as a trigger on the dashboard |
| `OnStartup` | `true` | Scan when the server starts |
| `Workers` | `16` | Parallel directory readers. Raise it for network shares |

An admin can also start a rescan with `POST /Library/Refresh`.

## Metadata (`JellyServer:Metadata:*`, `JellyServer:Tmdb:*`)

| Setting | Default | Notes |
|---|---|---|
| `Metadata:Concurrency` | `4` | Items looked up in parallel |
| `Metadata:NotFoundRetry` | `7.00:00:00` | Wait before asking again about an item no provider knew |
| `Metadata:FailedRetry` | `01:00:00` | Wait after a temporary failure |
| `Metadata:ReconcileInterval` | `24:00:00` | Default interval of the *Download Metadata* task. `0` removes the interval trigger |
| `Tmdb:ApiKey` | – | Same as the `TMDB_API_KEY` variable |

Per-library options (preferred language and country, which fetchers are on and their order, image limits) are set on the dashboard.

## Cache and user data

| Setting | Default | Notes |
|---|---|---|
| `Cache:QueryTtl` | 10 min | Redis TTL of cached query pages |
| `Cache:ItemTtl` | 24 h | Redis TTL of cached items |
| `UserData:ProgressFlushSeconds` | `5` | How often buffered playback progress is written to PostgreSQL. `0` writes every report directly |

Redis is also used for locks and the progress buffer. Without Redis the server still runs: every progress report is then written straight to PostgreSQL.

## Images (`JellyServer:Images:*`)

| Setting | Default | Notes |
|---|---|---|
| `Resize` | `true` | Resize images on request |
| `CacheDir` | – | Image cache folder (under the data directory by default) |
| `CacheMaxMB` | `512` | The cache is trimmed to this size, least recently used first. `0` = unlimited |
| `CacheMaxAgeDays` | `30` | The **Clean Image Cache** task (Dashboard → Scheduled Tasks → Maintenance, daily) deletes cached images unused for this many days. `0` = everything, so run the task once to empty the cache. Deleted pictures are made or downloaded again when asked for |
| `JpegQuality` | `85` | Quality of resized JPEG variants (clients without WebP) |
| `WebpQuality` | `80` | Quality of resized WebP variants. A client's own `quality` can only lower it. 10-99 |
| `MaxResizeConcurrency` | `2` | Resizes at once. Each holds a decoded bitmap; fewer is lighter on a small host |
| `RemoteFetch` | `true` | Allow downloading `http(s)` artwork referenced in NFO files |
| `Ingest:Enabled` | `true` | Compress artwork before storing it |
| `Ingest:Webp` | `true` | Store artwork as WebP (transparency included, no PNG). `false` = JPEG/PNG. Clients that do not send `image/webp` in `Accept` (or `format=webp`) get JPEG/PNG made from it either way |
| `Ingest:WebpQuality` | `70` | Quality of stored WebP artwork, 10-99. Lower = smaller files; 50 already looks fine for posters. Applies to artwork stored from now on |
| `Ingest:JpegQuality` | `65` | Quality of stored artwork when `Ingest:Webp` is `false` |
| `Ingest:KeepOriginal` | `false` | `true` also stores the original bytes. By default no full-size original is ever written, and an image that cannot be decoded is dropped |
| `Ingest:MaxConcurrency` | `1` | Images compressed at the same time. Low on purpose, so a small server stays responsive |
| `Ingest:MaxDownloadMB` | `20` | Larger remote images are skipped |
| `Ingest:MaxPixels` | `40000000` | Images with more pixels are rejected before decoding |
| `Ingest:Max` | see below | Bounding box per image type |

Stored artwork is shrunk to a box per type: Primary 500×750, Backdrop 1024×576, Logo 500×250, Thumb 640×360, Banner 1000×185, anything else 1000×1000. Artwork that sits next to your media (`poster.jpg`) is never rewritten.

## Transcoding (`JellyServer:Transcoding:*`)

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `true` | Without ffmpeg every client gets direct play |
| `FfmpegPath` / `FfprobePath` | `ffmpeg` / `ffprobe` | The image ships `jellyfin-ffmpeg` |
| `TempDir` | `{DataDir}/transcodes` | Disposable HLS segments. Compose points it at `/tmp/transcodes`, off the persistent volume |
| `SegmentSeconds` | `6` | HLS segment length |
| `Pipelines` | all registered | Order of preference, for example `["vaapi", "x264"]`. Unlisted encoders stay off |
| `Decoder` | first working hardware module | Hardware decoder feeding software encoders. Empty disables it |
| `MaxHeight` | `1080` | Taller sources are scaled down when re-encoded. `0` keeps the size |
| `AllowHevcEncoding` / `AllowAv1Encoding` | `false` | Let the playback decision pick HEVC or AV1 output |
| `IdleKillAfter` | `00:01:00` | Stop ffmpeg after this long without segment requests |
| `CleanupAfter` | `00:10:00` | Delete a job's segments after this much inactivity |

Server-wide processing options (tone mapping, deinterlacing, hardware decoding, audio, throttling, subtitles, fallback font) are edited on the dashboard's **Transcoding** page. Settings of each encoder are listed under [Playback & Transcoding](Playback-and-Transcoding#video-pipelines).

## Databases

| Setting | Notes |
|---|---|
| `ConnectionStrings:Postgres` | The shared server. Every module gets its own schema in it |
| `ConnectionStrings:<module>` | Moves one module to its own server or database. Modules: `identity`, `userdata`, `catalog`, `media`, `people`, `collections`, `settings`, `metadata`, `tasks`, `plugins` |
| `ConnectionStrings:Redis` | Redis address |

See [Deployment](Deployment) for pool sizes and examples.

## Logging

Logs are JSON lines on stdout, rotated by Docker (5 files of 20 MB). Set levels the usual .NET way, for example `Logging__LogLevel__Default: Warning`.

## Endpoints for operators

| Endpoint | Auth | Returns |
|---|---|---|
| `GET /health` | – | Status of every module database and Redis |
| `GET /metrics` | – | Prometheus: request counts and latency, cache hit rate, scan state |
| `GET /JellyServer/Status` | admin | Item counts, cache statistics, last scan, active transcoding pipelines and their load |
