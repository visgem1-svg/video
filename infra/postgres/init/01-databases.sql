-- One database per service (CONTRACTS rule 1). Runs once, on first start of an empty pgdata volume.
CREATE DATABASE registry OWNER vp;
CREATE DATABASE planner  OWNER vp;
CREATE DATABASE results  OWNER vp;
