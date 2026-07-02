# Deployment

Kanban runs two ways with **no code changes** — the app is config-driven and agnostic to
whether its infrastructure is dedicated or shared. This mirrors the ecosystem's Calendar app and
the reference pattern in [.standards/architecture.md](../.standards/architecture.md).

## Two modes

| Mode | Infra | When |
|---|---|---|
| **Standalone** | Bundles its own Postgres + Logto + **Caddy** reverse proxy via this repo's compose | Solo deploy, local dev, demos |
| **Integrated** | Points at the shared ecosystem Logto + Postgres + proxy; bundles none | Inside the ecosystem |

The app only ever reads configuration: an issuer URL, an API audience, client credentials, a
connection string, a Management API endpoint. Pointing those at bundled vs shared services is
the only difference between the two modes. In integrated mode the issuer host is the
**ecosystem-neutral** `auth.localhost` (not `auth.kanban.localhost`); that composition is owned
by the platform repo, not this one — see [ecosystem-integration.md](ecosystem-integration.md) §5.

## The reverse proxy (why it exists)

OIDC needs the **browser** (redirects) and the **app** (server-side back-channel: PAR / token /
JWKS / Management API) to reach Logto at the *same* URL. A containerized app can't use
`localhost:3001` — inside the container that's its own loopback. **Caddy** ([Caddyfile](../Caddyfile))
is the single entry point (port 8080) that both sides resolve to, routing by host:

| Host | Upstream |
|---|---|
| `localhost:8080` | kanban app |
| `auth.kanban.localhost:8080` | Logto OIDC + Management API |
| `admin.kanban.localhost:8080` | Logto admin console |

Browsers resolve `*.localhost` → `127.0.0.1` (so **only port 8080 needs forwarding**); containers
resolve the auth/admin hosts via Docker **network aliases** on the Caddy service. The app stays on
bare `localhost:8080`, so Logto redirect URIs are `http://localhost:8080/signin-oidc` and
`…/signout-callback-oidc`. In production, Caddy terminates TLS instead of `auto_https off`.

## Standalone (default for local dev)

Bundled `db`, `logto`, and `caddy` are gated behind the Compose **`standalone`** profile.

```bash
cp .env.example .env          # fill in the passwords (see below)
docker compose --profile standalone up --build
```

This starts Postgres (with the `logto` database + the least-privilege `kanban_app` role from
`db/init/`), Logto, the app, and Caddy. **First run only:** complete the
[Logto console checklist](auth.md#logto-registration-manifest-console-checklist) at
`http://admin.kanban.localhost:8080`, copy the resulting IDs into `.env`, and
`docker compose --profile standalone up -d` again. Then open `http://localhost:8080`.

## Integrated (ecosystem)

Run the app **without** the `standalone` profile so the bundled `db`/`logto` stay down; the
app joins the shared external network and its env points at the shared services:

```bash
docker compose up --build     # bundled db/logto are profile-gated, so not started
```

The shared Logto + Postgres + proxy are owned by the separate
[ecosystem-platform](https://github.com/davamix/ecosystem-platform) repo. Kanban attaches to them;
it does not manage them. Provisioning Kanban's Logto objects + DB role against the shared infra
uses the same manifests (the [console checklist](auth.md#logto-registration-manifest-console-checklist)
+ [ecosystem-integration.md](ecosystem-integration.md) §7).

## Database isolation & least privilege

One database per app; the app connects via a **dedicated least-privilege role**, never the
Postgres superuser (ASVS V13.2.2 — see [security/postgres-least-privilege.md](security/postgres-least-privilege.md)).

- `db/init/` (runs on a fresh volume) creates the `logto` database and the
  `kanban_app` `NOSUPERUSER NOCREATEROLE NOCREATEDB` role owning the `kanban` database, so
  EF `MigrateAsync` can create its own tables but nothing cluster-wide.
- `ConnectionStrings__Kanban` uses `kanban_app`. `POSTGRES_USER`/`POSTGRES_PASSWORD` stay
  the admin/break-glass credential.

## Configuration

All configuration is via environment variables (`.env` in dev). `__` maps to `:` for .NET
binding (`LOGTO__ISSUER` → `Logto:Issuer`). Document variable **names** in `.env.example`,
never values; secrets come from the environment only ([.standards/security.md](../.standards/security.md)).

| Variable | Mode | Purpose |
|---|---|---|
| `POSTGRES_PASSWORD` | standalone | admin password for the bundled Postgres |
| `KANBAN_APP_DB_PASSWORD` | both | password for the `kanban_app` role |
| `ConnectionStrings__Kanban` | both | Npgsql connection string (Username=`kanban_app`) |
| `LOGTO__ISSUER` / `LOGTO__AUDIENCE` | both | OIDC issuer + API resource indicator |
| `LOGTO__WEB__CLIENTID` / `LOGTO__WEB__CLIENTSECRET` | both | BFF client |
| `LOGTO__MANAGEMENT__ENDPOINT` / `…CLIENTID` / `…CLIENTSECRET` / `…RESOURCE` | both | Management API client |

## Production notes (later)

TLS terminates at a shared reverse proxy (no plaintext to clients). The container already runs
as a non-root user ([Dockerfile](../Dockerfile)). The image is built, scanned by Trivy, and
**published to GHCR on each `v*` release tag** — see [releasing.md](releasing.md).
