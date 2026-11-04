CREATE TABLE IF NOT EXISTS cameras (
    camera_id    text PRIMARY KEY,
    name         text NOT NULL,
    source_url   text NOT NULL,
    analysis_fps double precision NOT NULL DEFAULT 3,
    enabled      boolean NOT NULL DEFAULT true,
    plan_id      text NULL,
    tags         text[] NOT NULL DEFAULT '{}',
    updated_at   bigint NOT NULL
);
CREATE TABLE IF NOT EXISTS workers (
    worker_id    text PRIMARY KEY,
    capacity     integer NOT NULL,
    last_seen_ms bigint NOT NULL
);
-- one owner per camera (PRIMARY KEY on camera_id): double assignment is impossible at the DB level
CREATE TABLE IF NOT EXISTS assignments (
    camera_id text PRIMARY KEY REFERENCES cameras(camera_id) ON DELETE CASCADE,
    worker_id text NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_assignments_worker ON assignments(worker_id);
