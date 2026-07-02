# ADR 0002 — Caddy reverse proxy for the standalone stack

- **Status:** Accepted (2026-07-02)
- **Related:** [0001-infrastructure-bootstrap.md](0001-infrastructure-bootstrap.md), [../deployment.md](../deployment.md)

## Context

With the app, Postgres, and Logto all in Docker Compose, a **containerized** app cannot complete
OIDC when Logto is addressed as `http://localhost:3001`: inside the `kanban` container `localhost`
is the container itself, so the server-side back-channel (PAR / token / JWKS / Management API)
can't reach Logto. The OIDC issuer must be a single URL that resolves identically for the browser
*and* the app. This is the same problem Calendar solved with a reverse proxy, and the ecosystem
architecture already calls for "one reverse proxy".

## Decision

Add a **Caddy** service (standalone profile) as the single entry point on port **8080**, routing
by host:

| Host | Upstream |
|---|---|
| `localhost:8080` | kanban app |
| `auth.kanban.localhost:8080` | Logto OIDC + Management API (`logto:3001`) |
| `admin.kanban.localhost:8080` | Logto admin console (`logto:3002`) |

- **Browsers** resolve `*.localhost` → `127.0.0.1`, so only port 8080 needs forwarding.
- **Containers** resolve the auth/admin hostnames via Docker **network aliases** on the Caddy
  service, so the app's back-channel uses the *same* URL the browser does.
- Logto's `ENDPOINT`/`ADMIN_ENDPOINT` and the app's `LOGTO__ISSUER`/`LOGTO__MANAGEMENT__ENDPOINT`
  point at the Caddy hostnames. The app stays on bare `localhost:8080`, so the Logto redirect
  URIs (`/signin-oidc`, `/signout-callback-oidc`) are the simple `localhost` form.

In **integrated** mode the shared platform proxy owns routing and Logto is reached at the
**ecosystem-neutral** hosts (`auth.localhost` / `admin.localhost`), not the app-scoped
`auth.kanban.localhost` used here — see [ecosystem-integration.md](../ecosystem-integration.md) §5.

## Why

- It's the only approach that gives one issuer URL working from both sides without a per-host
  hack; it matches the ecosystem's "one reverse proxy" architecture and the eventual prod shape
  (TLS terminates at the shared proxy).
- Keeping the app on `localhost` keeps Logto redirect URIs simple and consistent with Calendar.

## Consequences

- The login round-trip is **cross-site** (`localhost` ↔ `auth.kanban.localhost`), so the OIDC
  correlation/nonce cookies are set `SameSite=Lax` (they survive the top-level GET callback and
  stay sendable over plain http in dev). See [AuthenticationExtensions.cs](../../src/KanbanApi/Auth/AuthenticationExtensions.cs).
- `caddy` and `logto` don't publish ports directly; only Caddy publishes 8080. Forward just that
  one port.
- Production swaps `auto_https off` for real TLS at the proxy; the routing model is the same.
- **Data Protection keys are persisted in Postgres** (`PersistKeysToDbContext`, table
  `DataProtectionKeys`) so auth/antiforgery cookies survive container recreates — otherwise every
  rebuild rotates the ephemeral keys and invalidates existing cookies ("key not found" / OIDC
  "Correlation failed").
