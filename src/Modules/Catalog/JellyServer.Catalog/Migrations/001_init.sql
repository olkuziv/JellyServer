CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE libraries (
    id              uuid PRIMARY KEY,
    name            text NOT NULL,
    collection_type text NOT NULL,
    paths           text[] NOT NULL,
    sort_order      int NOT NULL DEFAULT 0,
    last_scan_at    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- One row per browsable entity: Movie, Series, Season, Episode.
CREATE TABLE items (
    id                    uuid PRIMARY KEY,
    library_id            uuid NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
    parent_id             uuid,           -- Season for Episode, Series for Season, NULL at library root
    series_id             uuid,
    season_id             uuid,
    type                  text NOT NULL,  -- Movie | Series | Season | Episode
    name                  text NOT NULL,
    original_title        text,
    sort_name             text NOT NULL,
    search_text           text NOT NULL,  -- lower-cased name + original title, trigram-indexed
    path                  text NOT NULL,  -- absolute path to the video file or folder
    stream_url            text,           -- target of a .strm file
    container             text,
    overview              text,
    tagline               text,
    production_year       int,
    premiere_date         date,
    end_date              date,
    community_rating      real,
    critic_rating         real,
    official_rating       text,
    runtime_ticks         bigint,
    index_number          int,
    parent_index_number   int,
    genres                text[] NOT NULL DEFAULT '{}',
    studios               text[] NOT NULL DEFAULT '{}',
    provider_ids          jsonb NOT NULL DEFAULT '{}',
    people                jsonb,
    media_info            jsonb,          -- streams from NFO streamdetails or ffprobe
    images                jsonb NOT NULL DEFAULT '{}',
    subtitles             jsonb,          -- sidecar subtitle files
    collection_name       text,
    child_count           int NOT NULL DEFAULT 0,
    recursive_child_count int NOT NULL DEFAULT 0,
    size                  bigint NOT NULL DEFAULT 0,
    fingerprint           text NOT NULL DEFAULT '',
    date_created          timestamptz NOT NULL,
    date_modified         timestamptz NOT NULL DEFAULT now(),
    date_last_media_added timestamptz
);

CREATE INDEX items_lib_type_sort     ON items (library_id, type, sort_name);
CREATE INDEX items_lib_type_created  ON items (library_id, type, date_created DESC);
CREATE INDEX items_type_sort         ON items (type, sort_name);
CREATE INDEX items_type_created      ON items (type, date_created DESC);
CREATE INDEX items_parent            ON items (parent_id, index_number);
CREATE INDEX items_series            ON items (series_id, parent_index_number, index_number);
CREATE INDEX items_year              ON items (production_year);
CREATE INDEX items_search_trgm       ON items USING gin (search_text gin_trgm_ops);
CREATE INDEX items_genres            ON items USING gin (genres);

CREATE TABLE genres (
    id   uuid PRIMARY KEY,
    name text NOT NULL UNIQUE
);
