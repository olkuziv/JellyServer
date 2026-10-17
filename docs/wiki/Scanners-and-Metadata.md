# Scanners & Metadata

How files on disk become items in the catalog, and how data from the internet is merged in.

```
 disk ──► Scanner (per library type) ──► ScanCoordinator ──► Catalog / Media / People
                                              ▲                        │
                                              │                 library changed
 TMDB ──► Provider ──► Metadata module ───────┘ (laid over the item) ◄──┘
```

## Scanning

### Who does what

- A **scanner** is a module for one library type. It reads the file system and emits items. It never touches a database or a cache.
- The catalog's **`ScanCoordinator`** picks the scanner by library type, saves what scanners emit in batches, deletes what disappeared and invalidates caches.

Available scanners:

| Library type | Scanner | Rule |
|---|---|---|
| `movies` | `JellyServer.Scanners.Movies` | Every video or `.strm` in any folder is a movie |
| `tvshows` | `JellyServer.Scanners.TvShows` | Series → seasons → episodes |
| `mixed` | `JellyServer.Scanners.Mixed` | Movies and shows in one library. A folder counts as a series when it has `Season N` folders or `SxxExx` files |

Other types (music, books, home videos, music videos) are rejected with HTTP 400.

### Incremental by design

Every item stores a **fingerprint** of all the files that produced it: the video or `.strm`, the NFO, artwork and subtitles (name, size and mtime of each).

- **Unchanged item:** one `stat` per file, and nothing is parsed or written.
- **Changed item:** parsed once, then written in a batch through binary `COPY` into a temporary table, followed by a single `INSERT … ON CONFLICT`.
- **Parallel:** folders are read by `Scanner:Workers` readers (default 16). Raise this for network shares, where latency matters more than CPU.
- **Item ids come from paths**, so a rescan recognises the same item, and watched state and favourites are kept.

On a NAS with ~79k items: first scan 3 minutes, a rescan with no changes 39 seconds.

### When scans run

- On startup (`Scanner:OnStartup`) and every 12 hours (`Scanner:Interval`).
- From **Dashboard → Scheduled Tasks → Scan Media Library**.
- On `POST /Library/Refresh` (administrator).
- Scans report progress every 10 seconds, plus statistics per library.

After a scan changes anything, the *library generation* in Redis is bumped. That invalidates cached query pages. If nothing changed, the caches stay warm.

### What the file names mean

**Movies**

```
Movies/
  The Matrix (1999) [tmdbid-603]/
    The Matrix (1999).mkv
    The Matrix (1999).nfo
    poster.jpg
    The Matrix (1999).en.srt
```

**Versions of one movie or episode.** In a movie folder, every video must be named like the folder, optionally followed by ` - something`, `_something`, `.something` or `[something]`:

```
Movie (2020)/Movie (2020) - 1080p.mkv
Movie (2020)/Movie (2020) - 720p.mkv
```

They become one item with a version picker, as in Jellyfin. The file named exactly like the folder is the main one (otherwise the highest resolution, then the first by name). Episodes with the same season and episode number group the same way. Every file is a row of `media.sources`. The version's id is its `MediaSourceId`, and playback progress belongs to the main item.

**Sidecar files read next to the media:** `.nfo` (including `streamdetails`), artwork such as `poster.jpg`, and `.srt`, `.vtt`, `.ass` subtitles.

**`.strm` files** contain a URL instead of a video. They are first-class: they are scanned, matched to metadata and played like any other item (see [Playback & Transcoding](Playback-and-Transcoding#strm-files)).

### Where an item's data is kept

| Module | Keeps |
|---|---|
| Catalog | Names, hierarchy, sort and search keys, overview, artwork, genres, studios, provider ids, path |
| Media | Files and versions (path, `.strm` target, container, size, streams, sidecar subtitles) |
| People | Persons (once each) and credits per item in billing order |

A scan writes **files and credits first and the catalog row last**. If a write fails half-way, the old row and fingerprint remain and the next scan writes the item again.

## Metadata from the internet

The **Metadata** module looks items up through providers (`IMetadataProvider`; today `Providers.Tmdb`) and gives what it finds to the catalog.

### Local data wins

Merge order for every field:

1. **NFO values** and **artwork next to the files** (`poster.jpg`, …) are authoritative. An NFO title keeps the name.
2. The **provider fills what is empty.**
3. A manual refresh with *Replace all* lets the provider overwrite.

### Which item is it?

In this order:

1. An id from the NFO (`tmdbid`, `<uniqueid type="tmdb">`).
2. An id from the folder or file name: `[tmdbid-603]`, `{tmdb-603}`, `[imdbid-tt0133093]`, `[tvdbid-81189]`.
3. A search by name and year, accepted **only when the title and year really match**.

The found id is stored with the item, so the lookup is never repeated.

### What it brings

- Title and original title, overview, tagline, dates, rating, age rating for the library's country, genres, studios, collection, ids.
- Cast and crew with photos.
- Posters, backdrops and logos, in language order: the library's, English, no language.
- For series: seasons and episodes (name, overview, date, rating, still) with **one request per season.**

### When it runs

| Trigger | What |
|---|---|
| After every scan | New or changed items |
| *Download Metadata* task (startup, every 24 h) | Only items that still have no data. Shows progress, and *Stop* drops the queue |
| *Refresh All Metadata* task (Sunday 03:00) | Looks every item up again |
| `POST /Items/{id}/Refresh` | One item ("Refresh metadata" in clients) |

Retries: an item nobody knew is asked again after **7 days**, a temporary failure after **1 hour**. Requests are paced to 20 per second and retried on rate limits.

### Artwork

Provider images are stored as URLs first. The server downloads each one **once**, compresses it (see `Images:Ingest:*` in [Configuration](Configuration#images-jellyserverimages)), and stores only the compressed copy. Clients get resized variants like any other image. Image URLs carry tags derived from the content, so clients can cache them indefinitely.

### Raw answers

The provider's answers are kept in the module's own table (`metadata.item_remote`). The catalog gets the merged values in the item's columns. When a rescan rewrites an item, the stored answer is laid over it again, so no new request is needed.

### Per-library options

Set on the dashboard, per library:

- Preferred metadata language and country
- Which fetchers are on, and in which order
- Image limits (as in Jellyfin; backdrops default to 1)

Library options are stored in full and returned unchanged. For now only the folders, content type and display name affect scanning. The remaining options (trickplay, real-time monitoring, …) are saved and not used yet.

## Adding a source

A new metadata source (TVDB, OMDb, fanart.tv, …) is:

1. A project `JellyServer.Providers.<Name>` implementing `IMetadataProvider`.
2. One line in `Composition/AllModules.cs`.

The dashboard then offers the provider and libraries can order it. Step-by-step: [Writing a Module](Writing-a-Module#provider-modules).

## Adding a library type

A project `JellyServer.Scanners.<Name>` implementing `ILibraryScanner`, plus one line in `AllModules.cs`. Step-by-step: [Writing a Module](Writing-a-Module#scanner-modules).

## Related background tasks

| Task | Does |
|---|---|
| Scan Media Library | Reads the library folders |
| Download Metadata | Looks up only items without data |
| Refresh All Metadata | Looks every item up again |
| Clean up collections and playlists | Removes files, credits and playlist lines of items that no longer exist (skipped while a scan runs or when the catalog is empty) |
| Refresh People | Removes people without credits (every 7 days) |
