# Features

What the server does beyond browsing and playing a file. Everything here works through the standard Jellyfin API, so clients need no changes.

## Versions of one movie or episode

Files that are one movie in several versions (another resolution, voice-over, subtitles baked in) are **one item with a version picker**, as in Jellyfin. Naming rules and the way versions are stored are in [Scanners & Metadata](Scanners-and-Metadata#what-the-file-names-mean).

## Playlists and collections

The **Collections** module keeps playlists and box sets (schema `collections`). It provides Jellyfin's `/Playlists` and `/Collections` APIs, `DELETE /Items/{id}` for them, and shows both as items in `/Items`, search, favourites and "mark played".

### Playlists

- Belong to the user who made them. They can be **shared** with other users (who may or may not edit) or made **public**.
- Adding a series or season adds its episodes. A line is identified by its item id (Jellyfin's `PlaylistItemId`).
- They play in their order (`/Items?ParentId=<playlist>`), or shuffled with `SortBy=Random`.
- The *Playlists* view appears once the user can see one.
- When a user is deleted, their playlists go to the user they were shared with (editors first), private ones are deleted, and public ones stay.

### Box sets

- Administrators manage them, as with Jellyfin's collection-management right.
- They hold movies and shows as they are. Items are listed by premiere date, and playing a box set plays the episodes of the shows in it.
- The *Collections* library appears with the first box set. A movies library's *Collections* tab lists the box sets that contain a movie of that library.

### Artwork

An uploaded picture, otherwise the first item's.

### Not yet

Box sets created automatically from the provider's collection, and box set artwork from the provider. See the [Roadmap](Roadmap).

## People

Cast and crew are items of their own:

- `/Persons`, `/Persons/{name}` and `/Items/{personId}`
- Favourites, and the `PersonIds` / `PersonTypes` filters
- Person pages with filmography, and portraits from the provider

Each person is stored once, with a credit row per item in billing order. The weekly *Refresh People* task removes people no item credits any more.

## Playback progress

Clients report a position about every 10 seconds. Writing each report to PostgreSQL would be a storm of tiny writes, so:

1. Reports are **buffered in Redis**, one hash per user.
2. Every few seconds (`UserData:ProgressFlushSeconds`, default 5) the buffer is written to PostgreSQL in **one statement.** `0` writes every report straight through.
3. **Readers overlay the buffered positions**, so resume lists and item data show the latest position at once.
4. Stopping playback and marking items played **write through immediately.**

A crash loses nothing: pending hashes are renamed atomically before a flush and replayed afterwards, and the upsert only moves `last_played_at` forward. Without Redis every report is written directly.

## Scheduled tasks

The **Tasks** module runs everything on the dashboard's *Scheduled Tasks* page. A module registers an `IScheduledTask`; triggers (startup, daily, weekly, interval, max runtime) are stored in the `tasks` schema and an administrator can change them. Times of day are server local time, so set `TZ` in the container.

| Task | Does | Default triggers |
|---|---|---|
| Scan Media Library | Reads the library folders (disk) | Startup, every 12 h (`Scanner:Interval`, `Scanner:OnStartup`) |
| Download Metadata | Looks up only items without data (TMDB). Shows progress, and *Stop* drops the queue | Startup, every 24 h (`Metadata:ReconcileInterval`) |
| Refresh All Metadata | Looks every item up again | Sunday 03:00 |
| Clean up collections and playlists | Removes files, credits and playlist or collection lines of items that no longer exist. Skipped while a scan runs or when the catalog is empty | Every 24 h |
| Refresh People | Removes people without credits | Every 7 days |

Adding your own: [Writing a Module](Writing-a-Module#scheduled-tasks).

## Search

A trigram index handles search, including **Cyrillic** (`ґ` and `г` are normalised). Genres have a GIN index, and sorting and paging use composite indexes.

## Responses and images

- Heavy fields (`Overview`, `People`, `MediaSources`, …) are included only when the client asks for them with `Fields=`.
- Responses are compressed with Brotli or gzip.
- Images are resized once per 50 px size bucket and cached on disk. Concurrent requests for the same image are deduplicated, and resize work is capped at the CPU count. Image URLs carry tags derived from the content, so clients can cache them indefinitely.

## Observability

- **Logs** are JSON lines. Every 404 is logged with its query string, which is the main signal of a missing Jellyfin endpoint. Slow requests and slow SQL are warnings, and scans report progress every 10 s plus statistics per library.
- `GET /health` checks every module database and Redis.
- `GET /metrics` is Prometheus text format: request counts and latency buckets, cache hit rate, scan state.
- `GET /JellyServer/Status` (admin) shows item counts, cache statistics and last scan results.

See [Configuration](Configuration#endpoints-for-operators) and [Deployment](Deployment#monitoring).

## Not supported yet

Music, books, home videos, photos, Live TV and InstantMix are rejected or missing. The full list is on the [Roadmap](Roadmap).
