-- Jellyfin's LibraryOptions JSON, kept as sent so the dashboard can round-trip every setting.
ALTER TABLE libraries ADD COLUMN options jsonb NOT NULL DEFAULT '{}';
CREATE UNIQUE INDEX libraries_name ON libraries (lower(name));
