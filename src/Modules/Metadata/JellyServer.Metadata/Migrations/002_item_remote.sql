-- What the providers reported for an item (JSON of RemoteMetadata). The catalog lays it over the item's own columns and, after a
-- rescan rewrote the local values, asks for it again (IRemoteMetadataSource). It used to be a column of the catalog's items table;
-- here it can live on a server of its own.
CREATE TABLE item_remote (
    item_id uuid PRIMARY KEY,
    data    jsonb NOT NULL
);
