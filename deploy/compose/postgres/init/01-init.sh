#!/bin/bash
# First-boot initialisation for the incident database.
#
# Runs once, when the PostgreSQL data directory is created. It does two jobs:
#
#  1. Enables the pgvector extension, which adds the vector column type used
#     by semantic incident memory. The extension must be created by a
#     superuser, which is why it happens here and not in a normal migration.
#
#  2. Creates a separate low-privilege login role for the application.
#     The platform runs as kubesage_app, which can read and write rows but
#     cannot create, alter or drop tables. Schema changes are applied
#     separately using the owner role. This limits the damage a bug or an
#     injected query could do to the incident history.

set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE EXTENSION IF NOT EXISTS vector;

    -- Application role. NOSUPERUSER/NOCREATEDB/NOCREATEROLE are the defaults
    -- but are stated explicitly so the intent is obvious when reading this.
    CREATE ROLE kubesage_app
        LOGIN
        NOSUPERUSER
        NOCREATEDB
        NOCREATEROLE
        NOINHERIT
        PASSWORD '${KUBESAGE_APP_PASSWORD}';

    GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO kubesage_app;

    -- USAGE lets the role see and use objects in the schema.
    -- CREATE is deliberately NOT granted, so the role cannot add tables.
    GRANT USAGE ON SCHEMA public TO kubesage_app;

    -- Tables and sequences do not exist yet; migrations create them later as
    -- the owner. These default privileges make sure the application role
    -- automatically gets row access to whatever the owner creates from now on.
    ALTER DEFAULT PRIVILEGES FOR ROLE ${POSTGRES_USER} IN SCHEMA public
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kubesage_app;

    ALTER DEFAULT PRIVILEGES FOR ROLE ${POSTGRES_USER} IN SCHEMA public
        GRANT USAGE, SELECT ON SEQUENCES TO kubesage_app;
EOSQL

echo "KubeSage: pgvector enabled and least-privilege role kubesage_app created."
