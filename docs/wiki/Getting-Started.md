# Getting Started

From zero to a working server in a few minutes. You need Docker with Compose and a folder of media.

## 1. Start the stack

```bash
git clone <this repository>
cd JellyServer
cp .env.example .env          # optional, every value has a working default
docker compose up -d --build
```

Compose starts three containers:

| Container | Image | Role |
|---|---|---|
| `jellyserver` | built from the `Dockerfile` | The server. It also bundles the official jellyfin-web build (served at `/web`) and `jellyfin-ffmpeg` |
| `postgres` | `postgres:17` | Data, tuned for a read-heavy catalogue |
| `redis` | `redis:7-alpine` | Caches (512 MB, `allkeys-lru`, no persistence) |

The server waits until PostgreSQL and Redis are healthy, then applies the migrations of every module on its own. There is nothing to initialise by hand.

## 2. Tell it where your media is

Only two values matter on day one. Put them in `.env`:

```ini
MEDIA_PATH=/mnt/nas/media      # mounted read-only at /media inside the container
TMDB_API_KEY=your-key          # optional: metadata and artwork from TheMovieDb
```

The media folder is mounted **read-only** at `/media`. The server never writes into your library.

Other values you may want right away:

| Variable | Default | Purpose |
|---|---|---|
| `HTTP_PORT` | `80` | Host port for the web UI and the API (the container listens on 8096) |
| `SERVER_NAME` | `JellyServer` | Name shown to clients |
| `PUBLIC_URL` | `http://localhost` | Address advertised by UDP auto-discovery, for example `http://192.168.1.10`. Include the port if it is not 80 |
| `TZ` | `UTC` | Time zone of scheduled tasks |
| `STRM_MODE` | `auto` | How `.strm` files are played (see [Playback & Transcoding](Playback-and-Transcoding)) |

The full list is on the [Configuration](Configuration) page.

## 3. The first-run wizard

Open `http://<host>` (add `:<HTTP_PORT>` if you changed it). The first start shows Jellyfin's startup wizard:

1. **Language** for the server.
2. **Administrator**: name and password.
3. **Libraries**: choose a type and a folder. The folder picker browses *the container*, so pick folders below `/media`, for example `/media/movies`.
4. **Metadata language and country** for TheMovieDb lookups.
5. **Finish.**

Libraries and users live in the database and are managed from the dashboard afterwards. Nothing about them is configured through files or environment variables.

### Library types

| Type | What it expects |
|---|---|
| Movies | Every video or `.strm` in any folder is a movie |
| Shows | Series → `Season N` folders → episodes |
| Mixed | Movies and shows in one library. A folder counts as a series when it has `Season N` folders or `SxxExx` files |

Music, books, home videos and music videos are not supported yet and are rejected.

## 4. The first scan

The server scans on startup and then every 12 hours. Watch the log:

```bash
docker compose logs -f jellyserver
```

Scans report progress every 10 seconds plus statistics per library. Libraries fill in as the scan advances. After the scan, metadata is fetched from TheMovieDb in the background if you set a key.

Reference timings from a NAS with ~79k items: a first scan takes about 3 minutes, and a rescan with no changes about 39 seconds.

You can start a scan any time from **Dashboard → Scheduled Tasks → Scan Media Library**.

## 5. Connect a client

- **Browser:** `http://<host>`. jellyfin-web is served by the server itself.
- **Jellyfin apps** (Android TV, iOS, Tizen and others): add the server by address. With `PUBLIC_URL` set correctly they can also discover it over the network (UDP port 7359).
- **Samsung Tizen:** the app bundles its own copy of the web client. Point it at the server address.

## Useful checks

```bash
curl http://<host>/health      # every module database and Redis
curl http://<host>/metrics     # Prometheus text format
```

## Using a GPU for transcoding

```bash
# NVIDIA (NVIDIA Container Toolkit, or Docker Desktop on Windows)
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d --build

# Intel / AMD (Linux)
RENDER_GID=$(getent group render | cut -d: -f3) \
  docker compose -f docker-compose.yml -f docker-compose.vaapi.yml up -d --build
```

Details: [Playback & Transcoding](Playback-and-Transcoding).

## Updating

```bash
git pull
docker compose up -d --build
```

Migrations run automatically on startup, one module at a time. Read the release notes first: some upgrades (for example the catalog split) rebuild data from the files with a rescan and leave libraries looking empty until it finishes. Watched state and favourites survive, because item ids are derived from file paths.

## Troubleshooting

| Symptom | Check |
|---|---|
| Libraries look empty | Wait for the first scan, or follow the log. Folders must be under `/media` |
| A client cannot find the server | Set `PUBLIC_URL` to the address clients use, and make sure UDP 7359 is reachable |
| No posters or descriptions | `TMDB_API_KEY` is empty or wrong. The provider stays off without a key |
| Browser cannot play a `.strm` | Use `STRM_MODE=auto` or `proxy`. The source may block cross-origin requests |
| Something in a client does not work | Every 404 is logged with its query string. That line identifies the missing Jellyfin endpoint |

Next: [Configuration](Configuration) · [Architecture](Architecture)
