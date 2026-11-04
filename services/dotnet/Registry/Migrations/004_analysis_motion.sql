ALTER TABLE cameras ADD COLUMN IF NOT EXISTS analysis_stream_url text NULL;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS motion_enabled boolean NOT NULL DEFAULT false;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS motion_sensitivity text NOT NULL DEFAULT 'medium';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS motion_heartbeat_s double precision NOT NULL DEFAULT 5;
