-- The files of items moved to the Media module and their people to the People module (each can have a database of its own).
-- The table is emptied rather than rewritten: dropping columns frees nothing until a rewrite, and a rewrite needs as much free
-- space again, which a small server does not have. The next scan (it runs at startup) writes every item back, with its files and
-- people, and lays the stored provider metadata over it again; ids are derived from paths, so user data keeps matching.
TRUNCATE items;

ALTER TABLE items
    DROP COLUMN stream_url,
    DROP COLUMN container,
    DROP COLUMN people,
    DROP COLUMN media_info,
    DROP COLUMN subtitles,
    DROP COLUMN versions;
