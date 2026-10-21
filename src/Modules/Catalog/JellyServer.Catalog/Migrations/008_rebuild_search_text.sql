-- Rows saved by the metadata sink before the fix had an empty search_text. Rebuild it the way ItemNaming.SearchText does:
-- lower-case, ґ→г, ё→е, drop apostrophes, punctuation to spaces, collapse spaces.
UPDATE items
SET search_text = btrim(regexp_replace(
        translate(lower(name || ' ' || coalesce(original_title, '')), 'ґё:.,-_!?''’`', 'ге       '),
        '\s{2,}', ' ', 'g'))
WHERE search_text = '';
