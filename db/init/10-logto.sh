#!/bin/bash
# Runs once on a fresh Postgres data volume (docker-entrypoint-initdb.d). Creates the database
# and role for the bundled Logto identity provider, isolated from the app's data.
# LOGTO_DB_PASSWORD is injected from the db service environment (see docker-compose.yml / .env).
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<-EOSQL
  -- CREATEROLE is required: Logto creates per-tenant roles for row-level security during seeding.
  CREATE ROLE logto LOGIN CREATEROLE PASSWORD '${LOGTO_DB_PASSWORD}';
  CREATE DATABASE logto OWNER logto;
EOSQL

echo "10-logto.sh: created the 'logto' database + role."
