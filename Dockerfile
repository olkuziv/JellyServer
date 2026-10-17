# syntax=docker/dockerfile:1
ARG JELLYFIN_WEB_VERSION=10.11.5

# Builds natively and cross-compiles for the target (amd64 or arm64: ARM boards use the rkmpp/v4l2m2m pipelines).
# Only the project files, keeping the folder layout: the restore layer is reused until a dependency changes.
FROM --platform=$BUILDPLATFORM busybox AS manifests
COPY src/ /src/
RUN find /src -type f ! -name '*.csproj' -delete && find /src -type d -empty -delete

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY --from=manifests /src/ src/
RUN dotnet restore src/JellyServer/JellyServer.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/JellyServer/JellyServer.csproj -c Release -a $TARGETARCH --no-restore \
      --self-contained false -o /app /p:DebugType=none

# Official jellyfin-web build (GPL-2.0), served at /web for clients that load the UI
# from the server (iOS/Android apps, browsers). The Tizen app bundles its own copy.
FROM jellyfin/jellyfin:${JELLYFIN_WEB_VERSION} AS web

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# jellyfin-ffmpeg: Jellyfin's ffmpeg build, with the filters and hardware support the dashboard's Transcoding
# page offers (tonemap_cuda/tonemapx, rkmpp, recent QSV/VAAPI/AMF), installed from Jellyfin's apt repository.
# It bundles its own GPU user-space drivers (Intel iHD/i965 + oneVPL/MSDK runtimes, AMD radeonsi);
# NVENC needs nothing here either: the NVIDIA container runtime injects the driver.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && install -d -m 0755 /etc/apt/keyrings \
    && curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key | gpg --dearmor -o /etc/apt/keyrings/jellyfin.gpg \
    && . /etc/os-release \
    && echo "deb [signed-by=/etc/apt/keyrings/jellyfin.gpg] https://repo.jellyfin.org/${ID} ${VERSION_CODENAME} main" \
       > /etc/apt/sources.list.d/jellyfin.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends jellyfin-ffmpeg7 \
    && apt-get purge -y gnupg && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=web /jellyfin/jellyfin-web /app/web
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1 \
    NVIDIA_DRIVER_CAPABILITIES=compute,video,utility \
    JellyServer__Transcoding__FfmpegPath=/usr/lib/jellyfin-ffmpeg/ffmpeg \
    JellyServer__Transcoding__FfprobePath=/usr/lib/jellyfin-ffmpeg/ffprobe
VOLUME ["/data"]
EXPOSE 8096/tcp 7359/udp
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s \
  CMD curl -fsS http://localhost:8096/health || exit 1
ENTRYPOINT ["dotnet", "JellyServer.dll"]
