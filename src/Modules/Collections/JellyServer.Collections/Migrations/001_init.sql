-- Playlists and box sets. Small tables: a few hundred lists, a few thousand lines.
CREATE TABLE collections (
    id                    uuid PRIMARY KEY,
    kind                  text NOT NULL,                    -- Playlist | BoxSet
    name                  text NOT NULL,
    owner_id              uuid,                             -- playlists: the user who made it
    open_access           boolean NOT NULL DEFAULT false,   -- playlists: visible to every user
    shares                jsonb NOT NULL DEFAULT '[]',      -- playlists: [{"UserId": ..., "CanEdit": ...}]
    media_type            text,                             -- playlists: Video | Audio
    is_locked             boolean NOT NULL DEFAULT false,   -- box sets: no internet metadata
    images                jsonb NOT NULL DEFAULT '{}',
    date_created          timestamptz NOT NULL DEFAULT now(),
    date_modified         timestamptz NOT NULL DEFAULT now(),
    date_last_media_added timestamptz
);

CREATE INDEX collections_kind ON collections (kind, lower(name));

-- The lines of each list, in order. Items live in the catalog (another module, maybe another database): no foreign key, the catalog
-- tells this module when items go away.
CREATE TABLE entries (
    collection_id uuid NOT NULL REFERENCES collections (id) ON DELETE CASCADE,
    position      int NOT NULL,
    item_id       uuid NOT NULL,
    PRIMARY KEY (collection_id, position)
);

CREATE INDEX entries_item ON entries (item_id);
