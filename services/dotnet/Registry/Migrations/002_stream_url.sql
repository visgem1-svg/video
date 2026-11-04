-- contract 9.3: stream_url = real camera URL or 'publisher' (pushed into MediaMTX by someone else)
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS stream_url text NOT NULL DEFAULT 'publisher';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS provision_error text NULL;
