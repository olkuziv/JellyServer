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
- Scheduled tasks dashboard, `/health`, `/metrics`, `/JellyServer/Status`
- The reference deployment on 1 vCPU / 1 GB RAM with nine free 256 MB databases

## Next: playback

- **HEVC output.** The nvenc, qsv and vaapi pipelines can already encode HEVC, but the playback decision still always asks for H.264.
- **Embedded subtitle extraction.** Sidecar `.srt`, `.vtt` and `.ass` already work.
- A measured **Jellyfin comparison** on the same 1 vCPU / 1 GB host. For now the comparison is architectural.

## Next: metadata

- **Other sources:** TVDB, OMDb, fanart.tv. Each one is a `JellyServer.Providers.<Name>` project, so they can be added independently.
- **Manual search and "Identify"** for items the automatic match missed.
- **Automatic box sets** from the provider's collection ("Automatically add to collection" library option), and box set artwork from the provider.
- Library options that are saved but not yet used: metadata downloaders per type, trickplay, real-time monitoring.

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
4. **Small hosts count.** What does not fit 1 vCPU / 1 GB RAM and free-tier databases needs a good reason.
