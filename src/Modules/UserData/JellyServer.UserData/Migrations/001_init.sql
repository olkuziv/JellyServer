-- user_id and item_id point into other modules' databases: no foreign keys, consistency is by events/cleanup.
CREATE TABLE user_data (
    user_id        uuid NOT NULL,
    item_id        uuid NOT NULL,
    position_ticks bigint NOT NULL DEFAULT 0,
    play_count     int NOT NULL DEFAULT 0,
    played         boolean NOT NULL DEFAULT false,
    is_favorite    boolean NOT NULL DEFAULT false,
    last_played_at timestamptz,
    PRIMARY KEY (user_id, item_id)
);
CREATE INDEX user_data_resume ON user_data (user_id, last_played_at DESC) WHERE position_ticks > 0 AND NOT played;
CREATE INDEX user_data_fav    ON user_data (user_id) WHERE is_favorite;
CREATE INDEX user_data_played ON user_data (user_id) WHERE played;

CREATE TABLE display_preferences (
    user_id uuid NOT NULL,
    pref_id text NOT NULL,
    client  text NOT NULL,
    data    jsonb NOT NULL,
    PRIMARY KEY (user_id, pref_id, client)
);
