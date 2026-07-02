#!/bin/bash
# Runs once on a fresh Postgres data volume. Creates the least-privilege application role the
# Kanban app connects as (ASVS V13.2.2 — see docs/security/postgres-least-privilege.md).
# It owns the kanban database + public schema so EF MigrateAsync can create its own tables,
# but has no cluster-wide rights and cannot touch the separate 'logto' database.
# KANBAN_APP_DB_PASSWORD is injected from the db service environment.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
  CREATE ROLE kanban_app LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION
    PASSWORD '${KANBAN_APP_DB_PASSWORD}';
  ALTER DATABASE ${POSTGRES_DB} OWNER TO kanban_app;
  ALTER SCHEMA public OWNER TO kanban_app;
  GRANT ALL ON SCHEMA public TO kanban_app;
EOSQL

echo "30-app-role.sh: created the least-privilege 'kanban_app' role owning '${POSTGRES_DB}'."
