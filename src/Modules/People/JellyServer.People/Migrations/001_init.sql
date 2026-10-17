-- Every person once: the same actor in 300 episodes is one row, not 300 copies of the name and portrait URL.
CREATE TABLE persons (
    id          uuid PRIMARY KEY,   -- PersonIds.Of(name), the id clients see
    name        text NOT NULL,
    image_url   text,               -- portrait (http(s) URL)
    provider_id text                -- the provider's person id (TMDB)
);

-- Name order and prefix search ("C" collation: an index serves both ORDER BY and LIKE 'abc%').
CREATE INDEX persons_name ON persons ((lower(name) COLLATE "C"));

-- One row per item: its credits in billing order as three parallel arrays (person, PersonKind number, role or '').
CREATE TABLE credits (
    item_id    uuid PRIMARY KEY,
    person_ids uuid[] NOT NULL,
    kinds      smallint[] NOT NULL,
    roles      text[] NOT NULL
);

-- "Items with this person" (person pages, PersonIds filters).
CREATE INDEX credits_persons ON credits USING gin (person_ids);
