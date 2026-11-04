CREATE INDEX IF NOT EXISTS ix_recognitions_person_ts ON recognitions (person_id, ts DESC) WHERE person_id IS NOT NULL;
