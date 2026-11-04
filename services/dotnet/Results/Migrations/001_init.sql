CREATE TABLE IF NOT EXISTS recognitions (
    recognition_id text PRIMARY KEY,
    type           text NOT NULL,
    camera_id      text NOT NULL,
    frame_id       text NOT NULL,
    ts             bigint NOT NULL,
    value          text NOT NULL,
    person_id      text NULL,
    confidence     double precision NOT NULL,
    box            jsonb NULL,
    frame_key      text NULL,
    snapshot_key   text NULL,
    plan_id        text NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_recognitions_ts ON recognitions (ts DESC);
CREATE INDEX IF NOT EXISTS ix_recognitions_cam_ts ON recognitions (camera_id, ts DESC);

-- one row per camera/class/minute (NOT per frame)
CREATE TABLE IF NOT EXISTS detection_counts (
    camera_id text NOT NULL,
    class     text NOT NULL,
    minute_ts bigint NOT NULL,
    count     bigint NOT NULL,
    PRIMARY KEY (camera_id, class, minute_ts)
);
CREATE INDEX IF NOT EXISTS ix_detection_counts_minute ON detection_counts (minute_ts);

-- Kafka offsets committed in the same transaction as the aggregates (exactly-once aggregates)
CREATE TABLE IF NOT EXISTS consumer_offsets (
    topic       text NOT NULL,
    partition   integer NOT NULL,
    next_offset bigint NOT NULL,
    PRIMARY KEY (topic, partition)
);
