-- Which items the metadata system has dealt with, so a restart or a rescan does not ask the internet again.
CREATE TABLE item_state (
    item_id    uuid PRIMARY KEY,
    status     text NOT NULL,                       -- found | not_found | failed | skipped
    checked_at timestamptz NOT NULL DEFAULT now(),
    attempts   int NOT NULL DEFAULT 1
);
CREATE INDEX item_state_retry ON item_state (status, checked_at);