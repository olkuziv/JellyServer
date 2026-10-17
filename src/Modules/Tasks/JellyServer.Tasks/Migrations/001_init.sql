-- Triggers an administrator changed (NULL = the task's defaults) and how each task last ran, shared by all replicas.
CREATE TABLE task_state (
    id          text PRIMARY KEY,
    triggers    jsonb,
    last_result jsonb,
    updated_at  timestamptz NOT NULL DEFAULT now()
);