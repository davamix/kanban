# ADR 0001 — Infrastructure bootstrap & ecosystem integration

- **Status:** Accepted (2026-07-02)
- **Related:** [ecosystem-integration.md](../ecosystem-integration.md), [auth.md](../auth.md), [deployment.md](../deployment.md), [security/asvs-l2/](security/asvs-l2/), [0002-reverse-proxy-caddy.md](0002-reverse-proxy-caddy.md)

> This is the first entry in Kanban's decision log. **Every big change is recorded here**
> (context → decision → why → consequences) so the *why* survives the change. Keep entries
> short; link to the living docs for the *how*.

## Context

Kanban is a new **project/board manager** and the **second application** of an existing
ecosystem whose first app is **Calendar**. The ecosystem standards (vendored at `.standards/`,
pinned to `v0.5.0`) and the integration brief ([ecosystem-integration.md](../ecosystem-integration.md))
mandate a central identity provider (Logto), JWT-validating apps, one database + least-privilege
role per app, a single shared reverse proxy, and an OWASP ASVS 5.0 L2 baseline. Rather than
invent structure, Kanban **mirrors Calendar** (same stack, layout, CI/CD, security posture). This
ADR records the bootstrap decisions taken **before** any domain code, so the infrastructure and
the Logto wiring are in place for the implementation phase to build the board on.

## Decisions

1. **Stack = Calendar's.** ASP.NET Core minimal API (.NET 10) + a vanilla HTML/CSS/JS SPA served
   from `wwwroot`, EF Core + PostgreSQL, packaged with Docker. Lean stack: no Blazor, no Swagger
   UI, no Node/Tailwind toolchain.
2. **Identity provider: Logto**, dual-scheme auth ported from Calendar up front: a **BFF**
   (server-side OIDC code flow; tokens in an encrypted HttpOnly cookie) for the browser, and an
   **audience-scoped JWT-bearer** resource server (`aud = https://kanban.api`) for machine /
   inter-app callers. `/api/*` accepts either. The BFF only activates once a Logto web client is
   configured, so the app boots before the one-time console setup.
3. **Access model:** owner (creator) + assignees, identity always from the Logto `sub`. Applied
   to the domain entities (projects, tasks) in the implementation phase.
4. **One database per app**, accessed by a dedicated least-privilege `kanban_app` role
   (`NOSUPERUSER NOCREATEROLE NOCREATEDB`), created by `db/init/` on a fresh volume. Kanban never
   connects to Calendar's or Logto's database.
5. **Dual-mode deployment:** config-driven **standalone** (bundles Postgres + Logto + Caddy via a
   compose `standalone` profile) or **integrated** (points at the shared ecosystem infra owned by
   the platform repo). See [deployment.md](../deployment.md) and [0002](0002-reverse-proxy-caddy.md).
6. **Kanban → Calendar integration is one-way through Calendar's public API.** Creating a Kanban
   project mirrors a Calendar project owned by the *same user* via Logto **token exchange
   (RFC 8693, on-behalf-of)**; tasks are not mirrored. Kanban never touches Calendar's DB, and
   Calendar needs no change. The exchange caller + its Logto objects are provisioned in the
   integration milestone — see [ecosystem-integration.md](../ecosystem-integration.md) §6–§7.
7. **Security baseline: OWASP ASVS 5.0 L2**, tracked per-chapter under
   [security/asvs-l2/](security/asvs-l2/); gitleaks (pre-commit + CI), Trivy (fs + image), and a
   NuGet vulnerable-dependency audit gate CI/CD.
8. **Tooling:** a curated `.claude/agents/` reviewer set (security / access-control / architecture
   / migration). The design + integration-test author agents are added with the UI and test suite.

## Why

- **Mirror Calendar** rather than diverge: the ecosystem gains most from consistency, and the
  standards + Calendar are a proven reference. New names, same shapes.
- **Port the full auth stack now** (not with the domain): the BFF/JWT wiring, session lifetimes,
  antiforgery, and rate limiting are cross-cutting and identical to Calendar; getting them right
  once, before domain code, means the implementation phase only adds endpoints behind an already-
  correct auth surface. The user configures Logto against a running skeleton and can verify
  sign-in end-to-end before any board exists.
- **On-behalf-of over a machine token for the Calendar mirror:** Calendar sets a project's owner
  from the token `sub`; a plain client-credentials token would make *Kanban* the owner and the
  project would never appear on the user's Calendar. RFC 8693 token exchange carries the user's
  `sub` scoped to Calendar's audience — the only design that satisfies the requirement without a
  Calendar change.
- **Least-privilege DB role + secrets-from-env + scanning gates** are ASVS L2 requirements and
  cheap to establish at bootstrap; retrofitting them later is costly.

## Consequences

- The repo ships a buildable, runnable skeleton (health, OpenAPI, `/api/me`, `/api/users`, BFF
  `/login`/`/logout`) with an initial EF migration (`users` + `DataProtectionKeys`) but **no
  domain**. `docker compose --profile standalone up` and CI are green.
- The app has a hard dependency on a reachable Logto issuer (bundled in standalone mode) once the
  BFF is enabled; before that it runs as a JWT resource server.
- The implementation phase adds: the domain model + migrations (projects, tasks, boards/columns,
  assignees), the global query filter + owner checks (closing the V8 rows), the board SPA, the
  Calendar mirror (token-exchange client + Logto objects), and the test suite (closing the V8/V9
  test rows and adding the design/integration-test agents).
- Logto objects (API resource, BFF web app, M2M directory client, and later the exchange caller)
  are provisioned **manually** in the console per environment; only their **names** are recorded
  in `.env.example`, never values.
