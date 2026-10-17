-- What providers reported is now kept by the metadata module (its own schema, and its own server when configured):
-- the raw JSON was a copy of what the overlay already writes into the item's columns, about a third of this table.
ALTER TABLE items DROP COLUMN IF EXISTS remote;
