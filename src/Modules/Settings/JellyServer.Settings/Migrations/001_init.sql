-- Settings edited on the Jellyfin dashboard ("named configurations", e.g. "encoding"), shared by all replicas.
CREATE TABLE server_configuration (
    key        text PRIMARY KEY,
    data       jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
