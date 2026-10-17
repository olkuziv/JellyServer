CREATE TABLE users (
    id            uuid PRIMARY KEY,
    name          text NOT NULL UNIQUE,
    password_hash text NOT NULL DEFAULT '',
    is_admin      boolean NOT NULL DEFAULT false,
    is_hidden     boolean NOT NULL DEFAULT false,
    configuration jsonb NOT NULL DEFAULT '{}',
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_login_at timestamptz,
    last_active_at timestamptz
);

CREATE TABLE access_tokens (
    token        text PRIMARY KEY,
    user_id      uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id    text NOT NULL DEFAULT '',
    device_name  text NOT NULL DEFAULT '',
    client       text NOT NULL DEFAULT '',
    app_version  text NOT NULL DEFAULT '',
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX access_tokens_device ON access_tokens (user_id, device_id);
