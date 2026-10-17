-- Artwork of a library (uploaded in the dashboard): same shape as items.images.
ALTER TABLE libraries ADD COLUMN images jsonb NOT NULL DEFAULT '{}';
