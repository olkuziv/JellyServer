-- What internet providers reported (JSON of RemoteMetadata). It is laid over the item's own columns (see RemoteMetadataOverlay),
-- so search, filters and sorting keep working on plain columns, and a rescan puts it back after rewriting the local values.
ALTER TABLE items ADD COLUMN remote jsonb;
-- The name comes from local metadata (NFO title): providers must not rename the item.
ALTER TABLE items ADD COLUMN name_locked boolean NOT NULL DEFAULT false;