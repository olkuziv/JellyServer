-- One row per playable file: the main file of a movie or episode (id = item id, ordinal 0) and its other versions.
-- A version id is what clients call the MediaSourceId, so it is the primary key; the catalog item owns the rows by item_id.
CREATE TABLE sources (
    id            uuid PRIMARY KEY,
    item_id       uuid NOT NULL,
    ordinal       smallint NOT NULL,
    path          text NOT NULL,
    stream_url    text,           -- target of a .strm file
    container     text,
    size          bigint NOT NULL DEFAULT 0,
    runtime_ticks bigint NOT NULL DEFAULT 0,
    media_info    jsonb,          -- streams from NFO streamdetails or ffprobe
    subtitles     jsonb,          -- sidecar subtitle files
    fingerprint   text NOT NULL DEFAULT ''
);

CREATE INDEX sources_item ON sources (item_id, ordinal);
