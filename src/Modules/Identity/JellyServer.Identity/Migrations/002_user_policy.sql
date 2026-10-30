-- Jellyfin's UserPolicy as sent by the admin dashboard; is_admin / is_hidden / is_disabled stay as indexed copies of its flags.
ALTER TABLE users ADD COLUMN policy jsonb NOT NULL DEFAULT '{}';
ALTER TABLE users ADD COLUMN is_disabled boolean NOT NULL DEFAULT false;
