-- More files of one movie or episode ("alternate versions": another resolution, voice-over, ...). They are part of the main item,
-- not rows of their own, so listings and counts stay as they were. The index finds the item by a version id (MediaSourceId).
ALTER TABLE items ADD COLUMN versions jsonb;
CREATE INDEX items_versions ON items USING gin (versions jsonb_path_ops) WHERE versions IS NOT NULL;