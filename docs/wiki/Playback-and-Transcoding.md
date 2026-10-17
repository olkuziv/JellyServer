# Playback & Transcoding

How the server decides what to send to a client, how `.strm` streams reach it, and how video is converted when it has to be.

## The decision

The client sends a **DeviceProfile** with its PlaybackInfo request. The server picks the cheapest method that works:

| # | Method | When | Cost |
|---|---|---|---|
| 1 | **Direct play** | The client supports the container and codecs (for example MKV on Tizen) | None |
| 2 | **Remux to HLS** | The container is the problem (for example MKV on iOS) | Video is copied: CPU close to zero. Audio is copied if supported, otherwise converted to AAC (DTS on iOS or recent Samsung TVs) |
| 3 | **Transcode to H.264** | The client cannot decode the video codec | Real encoding. HDR is tone-mapped to SDR, and 4K is scaled down to 1080p |

HEVC and AV1 output exist in the encoders but are off by default (`Transcoding:AllowHevcEncoding`, `AllowAv1Encoding`). See the [Roadmap](Roadmap).

Codecs are read from the NFO `streamdetails`. When the NFO has none, the server runs `ffprobe` **once** on first playback and stores the result. Without ffmpeg, every client gets direct play.

### How segments are served

- The client receives a **complete VOD playlist up front**, so seeking works immediately.
- Asking for a segment far from ffmpeg's current position restarts ffmpeg at that point.
- ffmpeg is stopped after **60 s** without requests (for example while paused), and its files are deleted after **10 min**.
- Segments are written to `Transcoding:TempDir` (`/tmp/transcodes` in compose), off the persistent volume.

## `.strm` files

A `.strm` file holds the URL of the real stream. `Playback:StrmMode` decides who fetches it:

| Mode | Behaviour | Use when |
|---|---|---|
| `direct` | The client plays the URL itself. **No server bandwidth** | TVs and apps |
| `redirect` | The server answers `302` to the URL | The player follows redirects |
| `proxy` | The server relays the stream and rewrites HLS playlists with signed URLs | Sources that need it, or browsers blocked by CORS. Costs server bandwidth |
| `auto` | Clients listed in `Playback:ProxyClients` (default `Jellyfin Web`, i.e. browsers) are proxied. Every other client plays direct | Mixed households. **Default in compose** |

Set it with `STRM_MODE` or `JellyServer__Playback__StrmMode`.

## Video pipelines

Each way of producing video is a separate module behind `IVideoPipeline`. A pipeline owns the whole video path (decode, filters, encode). The server core adds input, audio and the HLS muxer.

| Pipeline | Hardware | Settings in `Transcoding:<Name>` (defaults) |
|---|---|---|
| `copy` | – | Built in. Always on, never limited |
| `nvenc` | NVIDIA | `Preset` p4 · `Cq` 23 · `MaxConcurrent` 3 · `Gpu` 0 · `MaxHeight` 1080 |
| `qsv` | Intel Quick Sync | `Device` /dev/dri/renderD128 · `Preset` veryfast · `GlobalQuality` 23 · `MaxConcurrent` 4 · `MaxHeight` 1080 · `ToneMap` true · `HwDecodeCodecs` |
| `vaapi` | Intel or AMD (Linux) | `Device` /dev/dri/renderD128 · `Qp` 24 · `MaxConcurrent` 4 · `MaxHeight` 1080 · `ToneMap` true · `HwDecode` |
| `amf` | AMD | `Preset` auto · `Qp` 24 · `MaxConcurrent` 4 |
| `videotoolbox` | Apple | `Preset` auto · `Quality` 60 · `MaxConcurrent` 4 |
| `rkmpp` | Rockchip (ARM boards) | `MaxConcurrent` 2 |
| `v4l2m2m` | ARM boards | `MaxConcurrent` 1 |
| `x264` | – (software H.264) | `Preset` veryfast · `Crf` 23 · `MaxConcurrent` 2 |
| `x265` | – (software HEVC) | `Preset` veryfast · `Crf` 28 · `MaxConcurrent` 1 |
| `svtav1` | – (software AV1) | `Preset` 10 · `Crf` 28 · `MaxConcurrent` 1 |

Set a value with, for example, `JellyServer__Transcoding__Nvenc__MaxConcurrent=2`. The older flat keys `H264Preset`, `H264Crf` and `MaxConcurrentEncodes` still work for x264.

### How a pipeline is chosen

1. **Order.** The default order is hardware first (nvenc, qsv, vaapi, then the others), then software (x264, …). `Transcoding:Pipelines` sets your own order, for example `["vaapi", "x264"]`. Encoders that are not listed stay off.
2. **Probing.** At startup the server checks which pipelines the installed ffmpeg supports, **runs a real test encode** for hardware ones, and logs the result. A pipeline whose test fails is skipped, so on a machine without that GPU nothing breaks.
3. **Suitability.** `CanHandle` says whether a pipeline can produce the requested output from the source.
4. **Concurrency.** Each pipeline has its own `MaxConcurrent`. When one is full, the next suitable one takes the job.
5. **Stickiness.** A job keeps its pipeline across seeks.
6. **Failures.** If ffmpeg fails before producing a segment, the job moves to the next pipeline. A pipeline that fails on **three different items in a row** is benched for 5 minutes. Repeated failures on one broken file do not count.

`GET /JellyServer/Status` (admin) shows the active pipelines, their load and any benching.

### HDR

HDR is tone-mapped to SDR **on the GPU only where the probe proved it works**:

- **NVENC** keeps HDR on the GPU only with an ffmpeg that has `tonemap_cuda`. The image ships `jellyfin-ffmpeg`, which does. With a stock ffmpeg, HDR goes to x264.
- **QSV and VAAPI** tone-map on the GPU when `ToneMap` is on and the probe passes.
- Otherwise HDR sources go to x264.

### Hardware decoding with software encoding

The software encoders accept frames from a hardware decoder (`IVideoDecoder`). `Transcoding:Decoder` picks which hardware module decodes (`null`: the first working one, empty: disabled). This helps a machine with a weak CPU and a GPU that cannot encode what you need.

## GPU setup

The Docker image includes `jellyfin-ffmpeg` with the GPU user-space drivers for Intel and AMD. NVENC needs nothing inside the image: the NVIDIA container runtime injects the driver.

### NVIDIA

Requires the NVIDIA Container Toolkit on Linux, or Docker Desktop with WSL2 on Windows.

```bash
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d --build
```

The override reserves one GPU for the container.

### Intel and AMD (Linux)

```bash
RENDER_GID=$(getent group render | cut -d: -f3) \
  docker compose -f docker-compose.yml -f docker-compose.vaapi.yml up -d --build
```

The override passes `/dev/dri` into the container and adds the host's `render` group. Use `Transcoding:Qsv:Device` / `Transcoding:Vaapi:Device` if your device is not `renderD128`.

### ARM boards

The image builds for `amd64` and `arm64`. Rockchip boards use the `rkmpp` pipeline, and other ARM boards use `v4l2m2m`.

### Check that it worked

```bash
docker compose logs jellyserver | grep -i pipeline
curl -H "Authorization: …" http://<host>/JellyServer/Status
```

The log lists every pipeline and whether its probe passed.

## Server-wide processing options

The dashboard's **Transcoding** page edits: tone-mapping (algorithm, mode, range, peak), VPP tone-mapping, deinterlacing, hardware decoding per codec, hardware encoding options (low-power H.264/HEVC), software preset and CRF, audio, throttling, segment deletion, subtitles and the fallback font. They are handed to each pipeline as a per-job snapshot.

## Settings summary

`Transcoding:Enabled`, `FfmpegPath`, `FfprobePath`, `TempDir`, `SegmentSeconds` (6), `Pipelines`, `Decoder`, `MaxHeight` (1080), `AllowHevcEncoding`, `AllowAv1Encoding`, `IdleKillAfter` (60 s), `CleanupAfter` (10 min). Full table: [Configuration](Configuration#transcoding-jellyservertranscoding).

## Subtitles

Sidecar `.srt`, `.vtt` and `.ass` files are supported. Extracting *embedded* subtitles is on the [Roadmap](Roadmap).

## Writing an encoder

An encoder is one project under `src/Transcoding/Encoders/{Hardware,Software}/JellyServer.Transcoding.<Name>`, referencing `JellyServer.Transcoding.Abstractions`. It implements `IVideoPipeline`:

| Member | Purpose |
|---|---|
| `Id` | Name used in `Transcoding:Pipelines`, logs and status |
| `OutputCodecs` | Codecs it can encode to (read after the probe) |
| `MaxConcurrent` | Simultaneous jobs; zero or less is unlimited |
| `AcceptsDecodedInput` | Software encoders return `true`: they take frames from an `IVideoDecoder` |
| `ProbeAsync(ffmpeg, ct)` | Run at startup and when settings change. Return `false` and the pipeline is not used. Publish the probed state atomically: jobs run concurrently with a re-probe |
| `CanHandle(job)` | Can it produce the target from this source? |
| `Build(job)` | Video arguments for one ffmpeg run. **Keyframes must fall exactly on segment boundaries**, because the playlist is built before encoding starts |

A hardware module may also register an `IVideoDecoder` under the same `Id` for the decode-only case.

Expose an `Add<Name>Pipeline(this IServiceCollection, IConfiguration transcoding)` extension that binds an options class from `Transcoding:<Name>` and registers the pipeline. Then call it in `Program.cs`. **Registration order is the default preference**, so register hardware before software. Add a test project next to the existing encoder tests.
