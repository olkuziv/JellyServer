-- Box sets made automatically remember which provider collection (TMDb collection id) they stand for, so a rescan can find and
-- rename them when the library's language changes.
ALTER TABLE collections ADD COLUMN provider_id text;

CREATE INDEX collections_provider ON collections (provider_id) WHERE provider_id IS NOT NULL;
