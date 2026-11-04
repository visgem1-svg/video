-- contract 9.5: drain / capacity override on workers, pin + restart generation on cameras
ALTER TABLE workers ADD COLUMN IF NOT EXISTS capacity_override integer NULL;
ALTER TABLE workers ADD COLUMN IF NOT EXISTS draining boolean NOT NULL DEFAULT false;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS pinned_worker text NULL;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS generation integer NOT NULL DEFAULT 0;
