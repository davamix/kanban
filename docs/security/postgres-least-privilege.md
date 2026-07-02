# Postgres least-privilege application role (ASVS L2 V13.2.2)

The app connects to Postgres as **`kanban_app`** — a `LOGIN NOSUPERUSER NOCREATEROLE
NOCREATEDB NOREPLICATION` role that **owns the `kanban` database and its objects** (so EF
`MigrateAsync` at startup can create/alter its own tables) but has no cluster-wide rights. The
bootstrap superuser (`POSTGRES_USER`, default `kanban`) is used only for administration /
break-glass and to run the bundled Logto's database.

This satisfies V13.2.2 (*backend components run with least privilege*): a compromise of the
app's DB credential cannot touch the separate `logto` database, create roles, or perform
superuser operations.

## Fresh volume (dev, CI, new deployments)

`db/init/*.sh` runs automatically on a fresh data volume (alphabetical order):

- `10-logto.sh` — creates the `logto` database (+ its role) for the bundled Logto.
- `30-app-role.sh` — creates `kanban_app` and hands it the `kanban` database + `public`
  schema, so the app migrates as `kanban_app`, owning everything it creates.

Set before first start (`.env`):

```
POSTGRES_PASSWORD=<admin password>          # bootstrap superuser
KANBAN_APP_DB_PASSWORD=<strong password>    # the kanban_app role
ConnectionStrings__Kanban=Host=db;Port=5432;Database=kanban;Username=kanban_app;Password=<KANBAN_APP_DB_PASSWORD>
```

## Existing volume — one-time migration

The init scripts do **not** run on a populated volume. Migrate in place (as the superuser):

```bash
docker exec -it kanban-db psql -U kanban -d postgres \
  -c "CREATE ROLE kanban_app LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION PASSWORD '<pwd>';"
# then transfer ownership of the kanban DB + its tables/sequences to kanban_app,
# and switch ConnectionStrings__Kanban to Username=kanban_app.
```

Reversible: the bootstrap superuser still exists; revert the connection string to roll back.

## Bundled Logto role has `CREATEROLE` (accepted risk)

`db/init/10-logto.sh` creates the `logto` role with **`CREATEROLE`** because Logto creates
per-tenant roles for row-level security during seeding (without it, seed fails:
*"Only roles with the CREATEROLE attribute may create roles"*). `CREATEROLE` is cluster-wide for
role management, so the `logto` role's reach isn't confined to the `logto` database — a noted
deviation from "credentials scoped to its own database." **Accepted for the bundled standalone
stack** (dev/POC, single throwaway Postgres). In an integrated/production deployment, Logto runs
against the shared Postgres owned by the platform repo, so this doesn't widen the app's blast
radius. The app's own `kanban_app` role remains least-privilege (no `CREATEROLE`).

## Operational caveat

Installing a **new** Postgres extension is a superuser operation. If a future migration adds
one, pre-install it as the superuser (e.g. `CREATE EXTENSION IF NOT EXISTS <name>`) before the
app migrates — the same pattern the init scripts use.
