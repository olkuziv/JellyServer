-- Documents plugins keep (IPluginStorage): one row per plugin, collection and key.
CREATE TABLE plugin_kv (
    plugin      text        NOT NULL,
    collection  text        NOT NULL,
    key         text        NOT NULL,
    value       jsonb       NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (plugin, collection, key)
);
