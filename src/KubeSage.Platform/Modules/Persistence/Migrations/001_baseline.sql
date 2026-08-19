-- Baseline schema.
--
-- This script only establishes the foundations that everything else depends
-- on. Domain tables are added by later migrations so that each change to the
-- incident model is a separate, reviewable script.

-- pgvector adds the "vector" column type used by semantic incident memory.
-- Creating an extension needs elevated rights, which is why migrations run
-- with the schema-owner connection string rather than the application role.
CREATE EXTENSION IF NOT EXISTS vector;

-- Small key/value table for facts about the platform installation itself,
-- for example when the schema was first created. It gives the health endpoint
-- something real to read, and later phases use it to record bookkeeping that
-- must survive a restart but does not deserve its own table.
CREATE TABLE IF NOT EXISTS platform_metadata (
    key             text        PRIMARY KEY,
    value           text        NOT NULL,
    updated_at_utc  timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

INSERT INTO platform_metadata (key, value)
VALUES ('schema_initialised_at_utc', to_char(now() AT TIME ZONE 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
ON CONFLICT (key) DO NOTHING;
