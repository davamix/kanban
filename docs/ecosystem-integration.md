# Ecosystem integration brief

**Purpose.** A self-contained context document for **bootstrapping a new ecosystem app** in an
isolated repo/devcontainer, and integrating it with the existing **Calendar** app. The first
consumer is **Kanban** (a project/board manager). Paste this into the initial prompt when you start
defining the new app: it captures how the ecosystem is deployed, how the shared `.standards/`
submodule works, the infra decisions already made, and the concrete Logto/DB objects the new app
must create.

The *why* behind these decisions is recorded as an ADR in Calendar's repo
(`docs/decisions/0003-cross-app-integration.md`). This brief is the *how* — and is written to stand
on its own without the Calendar repo present.

---

## 1 · How the shared standards work (`.standards/`)

Every app in the ecosystem vendors a **git submodule** at `.standards/`, pinned per project to a
tag of <https://github.com/davamix/standards>. It carries the cross-project rules; the app's own
specs live in its `docs/`.

- **Hydrate it** (empty after clone): `git submodule update --init --remote .standards`
- **Import the shared docs** into the app's `CLAUDE.md` so the assistant always has them:
  `@.standards/architecture.md`, `@.standards/security.md`, `@.standards/api-design.md`.
- **Shared vs local:** the *rules* (architecture, security baseline, API conventions) are shared
  and identical across apps; the *status/evidence* (e.g. the ASVS L2 tracker rows) are app-local.
- **Security wiring shipped with the standards** (copy verbatim into the new app):
  - `templates/pre-commit` → `.githooks/pre-commit` — gitleaks secret scan before every commit.
  - `templates/post-create.sh` → `.devcontainer/post-create.sh` — hydrates `.standards`, installs
    gitleaks, enables the hook; wired via the devcontainer `postCreateCommand`. Extend per app.

**Build the new app like Calendar:** lean stack (no Blazor/Swagger UI/Node toolchain unless
justified), an ADR per big change in `docs/decisions/`, a curated read-only reviewer set in
`.claude/agents/` (security / access-control / architecture / migration), and an OWASP ASVS 5.0 L2
tracker in `docs/security/asvs-l2/`.

## 2 · Ecosystem principles (the constraints the design obeys)

- **One shared instance of each capability, logically partitioned** — one Postgres server, one
  Logto, one reverse proxy; per-app isolation *inside* each, not one of everything per app.
- **Centralized identity.** Logto issues tokens; every app only **validates JWTs** (issuer pinned,
  audience checked, JWKS signature, lifetime, `alg:none` rejected). Identify users by `iss`+`sub`,
  **never** from a client-supplied field.
- **One database per app**, accessed by a dedicated least-privilege role.
- **Inter-app communication is synchronous REST first**, each app behind the shared proxy; an event
  bus (NATS) is added only when apps must react to each other's changes.
- **Service-to-service auth is OAuth2** (client-credentials, or token exchange for on-behalf-of);
  **no shared static API keys** between apps.
- **Two deployment modes, no code change:** **standalone** (the app bundles its own
  Postgres + Logto + Caddy behind a compose `standalone` profile) and **integrated** (bundled infra
  profile-gated off; the app points at the shared ecosystem Logto + Postgres + proxy). The app only
  ever reads configuration — issuer URL, API audience, client credentials, connection string.

## 3 · Integrated topology

```
                                  ┌───────────┐
                                  │  Browser  │
                                  └─────┬─────┘
                                        │  HTTPS   (north–south)
                                        ▼
                    ╔═══════════════════════════════════════╗
                    ║      Shared reverse proxy (Caddy)      ║  one public entry
                    ║          routes by hostname            ║  TLS terminates here
                    ╚═══╤═══════════════╤═══════════════╤════╝
         calendar.<dom> │  kanban.<dom> │   auth.<dom> / admin.<dom>
                        ▼               ▼               ▼
                ┌────────────┐   ┌────────────┐   ┌──────────────┐
                │  Calendar  │◀──┤   Kanban   │   │    Logto     │
                │    app     │   │    app     │   │  (identity)  │
                └─────┬──────┘   └─────┬──────┘   └──────┬───────┘
                      │   ▲            │                 │
                      │   └────────────┘                 │  both apps validate
                      │   east–west REST                 │  JWTs from the SAME
                      │   http://calendar:8080           │  issuer.  audiences:
                      │   Bearer  aud=calendar.api        │    calendar.api
                      │   (user sub via token-exchange)  │    kanban.api
          connects    │            connects │            │ uses logto DB
          ONLY to     ▼            ONLY to  ▼            ▼
                ┌───────────────────────────────────────────────┐
                │             Shared Postgres server            │
                │   ┌──────────┐   ┌──────────┐   ┌──────────┐  │
                │   │ calendar │   │  kanban  │   │  logto   │  │  one DB +
                │   │    DB    │   │    DB    │   │    DB    │  │  least-priv
                │   └──────────┘   └──────────┘   └──────────┘  │  role per app
                │   role: calendar_app  kanban_app   logto      │
                └───────────────────────────────────────────────┘
```

**Shared (one instance each):** reverse proxy · Logto · Postgres server.
**Per-app, isolated:** one DB + least-privilege role · one API audience · one BFF client.
Kanban reaches Calendar's *data* only through Calendar's *API* — never its DB.

## 4 · Ownership / deployment boundaries

```
┌─────────────────────────────────────────────────────────────────────┐
│  Ecosystem / platform repo   — owns shared infra + integrated stack  │
│    • Shared Postgres   • Shared Logto   • Shared reverse proxy        │
│    • integrated docker-compose (apps run with bundled infra OFF):    │
│          image: ghcr.io/<org>/calendar:<tag>                         │
│          image: ghcr.io/<org>/kanban:<tag>                           │
│    • this compose is ALSO the local "develop the integration" stack  │
└─────────────────────────────────────────────────────────────────────┘
            ▲ pulls image                          ▲ pulls image
            │                                       │
 ┌──────────────────────────┐         ┌──────────────────────────┐
 │  Calendar repo           │         │  Kanban repo  (new)      │
 │   • app + Dockerfile     │         │   • app + Dockerfile     │
 │   • `standalone` profile:│         │   • `standalone` profile:│
 │     bundles own infra    │         │     bundles own infra    │
 │   • publishes → GHCR      │         │   • publishes → GHCR      │
 │   • references Kanban: NO │         │   • references Calendar:NO│
 └──────────────────────────┘         └──────────────────────────┘
```

Neither app's own compose mentions the other. The "run them together" composition lives **only** in
the ecosystem/platform repo. To develop the integration locally, run that ecosystem compose (shared
infra + both GHCR images), not a copy of Calendar nested inside Kanban.

## 5 · Shared infrastructure specifics

**Postgres — shared server, one DB + role per app.** Calendar already has the `calendar` database
owned by a `calendar_app` role that is `NOSUPERUSER NOCREATEROLE NOCREATEDB` (so EF migrations can
create its own tables but nothing cluster-wide). **Kanban adds:** a `kanban` database + a
`kanban_app` least-privilege role with the same restrictions, created by an init script on a fresh
volume (mirror Calendar's `db/init/` pattern). The app connects as `kanban_app`, never the Postgres
superuser. Kanban never connects to the `calendar` database.

**Reverse proxy — one shared entry** (owned by the platform repo's `Caddyfile`). Add a
`kanban.localhost` route alongside `calendar.localhost`. The Logto host is **ecosystem-neutral**
(`auth.localhost` / `admin.localhost`), shared by both apps — not the `auth.calendar.localhost` name
Calendar uses in solo standalone mode. Containers resolve the auth host via a network alias on the
proxy so each app's server-side OIDC back-channel uses the same URL the browser does. In production
the proxy terminates TLS.

**Logto — one shared instance.** Both apps pin the **same issuer**; each keeps its **own API
audience**. See §7 for the exact objects Kanban registers.

## 6 · Kanban ↔ Calendar integration

**What is mirrored:**

| Kanban action | Effect in Calendar |
|---|---|
| Create a Kanban **project** (name, dates, assignees) | Kanban calls Calendar's API to create a Calendar **project** owned by the same user, with the same assignees |
| Create a Kanban **task** | **Nothing** — tasks are not mirrored |
| A user is a Kanban project assignee | They see the mirrored project on their Calendar (Calendar's existing owner+assignee model) |

`project`/`task` in Kanban are **distinct domain entities** from Calendar's; only a few properties
(name, dates, assignees) overlap. Kanban-task assignees are restricted to that project's assignees —
this is **Kanban-internal** filtering over shared Logto identities and needs nothing from Calendar.
Because everything goes through Calendar's public API, **Calendar needs no change**.

**Why on-behalf-of (and how Logto does it):** Calendar sets a project's owner from the token
`sub`, never from the request body. A plain machine (client-credentials) token would make *Kanban*
the owner, and the project would never appear on the user's Calendar. So Kanban must call Calendar
with a token that carries the **user's** `sub` but is scoped to Calendar's audience. Logto provides
this via **user impersonation + token exchange (RFC 8693)** — note it **cannot** directly swap a
live session token; the exchange's `subject_token` must be an impersonation **subject token** minted
through the Management API:

```
User (browser, signed into Kanban)
  │ 1. create Kanban project  (name, dates, assignees…)
  ▼
Kanban app ──2. POST /api/subject-tokens  (Management API, M2M token)──▶ Logto
  │            body { userId: <user's sub>, context: {...} }
  │◀──────────3. short-lived, one-time subjectToken ──────────────────────
  │
  │ 4. POST /oidc/token   grant_type=…:token-exchange
  │      subject_token=<subjectToken>
  │      subject_token_type=urn:ietf:params:oauth:token-type:access_token
  │      resource=https://calendar.api   scope=…   (Basic auth: the exchange client)
  ▼
Logto ──5. access token { sub=user, aud=https://calendar.api } ──▶ Kanban app
  │
  │ 6. POST /api/projects            Authorization: Bearer <token>
  ▼
Calendar app
  │   owner = token.sub  (= the human; never from the payload)
  │ 7. POST /api/projects/{id}/assignees ×N   (each = a Logto sub)
  ▼
calendar DB → project owned by the user → shows on THEIR Calendar
              ✓ zero code change in Calendar
```

(Optional: pass the user's own token as `actor_token` in step 4 to get an `act` claim — an audit
trail of *Kanban acted on behalf of this user*.)

**Calendar API contract Kanban consumes** (audience `https://calendar.api`; bearer token from the
exchange above; JSON is camelCase; dates are `yyyy-MM-dd`):

- `POST /api/projects` — body `{ "name", "description"?, "startDate", "endDate", "color"? }`.
  Owner is set server-side from the token `sub` (the creator is auto-added as an assignee). Returns
  the created project incl. `id`.
- `POST /api/projects/{id}/assignees` — body `{ "userId": "<logto sub>" }`, **owner-only**. Call
  once per additional assignee. (Assignee `userId` values are Logto `sub`s — portable because both
  apps share Logto.)

Discover the live contract from Calendar's generated OpenAPI document rather than hardcoding shapes.

## 7 · Logto objects to create for Kanban

Provisioned once per environment in the shared Logto admin console; record the resulting IDs in
Kanban's environment (document **names** only, never values).

1. **API Resource — Kanban's own audience.** Indicator `https://kanban.api`
   (`KANBAN__AUDIENCE`). Enable `offline_access`. Kanban's bearer scheme validates this `aud`.
2. **Application → Traditional Web App — Kanban's BFF** (browser auth, tokens server-side in an
   encrypted HttpOnly cookie, never exposed to JS):
   - Redirect URI `http://<kanban-host>/signin-oidc`; post-sign-out
     `http://<kanban-host>/signout-callback-oidc` (register the exact callback the framework sends,
     not just `/`).
   - Copy `ClientId`/`ClientSecret` → `KANBAN__WEB__CLIENTID` / `KANBAN__WEB__CLIENTSECRET`.
3. **The Kanban→Calendar caller — two capabilities** (one app may hold both, or split them):
   - **M2M with Management API access** — mints impersonation **subject tokens** via
     `POST /api/subject-tokens` (the `userId` is the acting user's `sub`).
   - **Confidential client with "Allow token exchange" enabled** (Console → Applications →
     [app] → *Token exchange*; **off by default**), **granted the Calendar API resource**
     (`https://calendar.api`) — performs the RFC 8693 exchange in §6. The toggle lives on **this
     caller**, never on Calendar (Calendar only validates the resulting JWT).
   - Copy creds → `KANBAN__EXCHANGE__CLIENTID` / `…CLIENTSECRET` (+ the Management-API M2M creds);
     configure the Calendar API base URL it calls.
4. **(If Kanban needs its own user directory)** the same (or a further) M2M client with the
   built-in **"Logto Management API access"** role — the pattern Calendar uses for its assignee
   picker (token request asks `scope=all`; keep `resource=https://default.logto.app/api`).

All apps pin the same `LOGTO__ISSUER` (the ecosystem-neutral host). Kanban's own issuer/audience env
mirrors Calendar's `LOGTO__ISSUER` / `LOGTO__AUDIENCE` contract.

## 8 · Distribution (GHCR)

Each app's CI builds and pushes a versioned image to `ghcr.io/<org>/<app>` (digest-pinnable),
scanned by Trivy before deploy, running as a non-root user. The ecosystem/platform repo's integrated
compose references those tags. No app embeds another app's image in its own compose.

## 9 · Status of the earlier open items

- **Platform repo — created:** [davamix/ecosystem-platform](https://github.com/davamix/ecosystem-platform)
  owns the shared Postgres/Logto/Caddy and the integrated compose (pulls each app from GHCR). Its
  `docs/joining-the-shared-stack.md` has the concrete host map + per-app checklist.
- **Logto on-behalf-of — confirmed:** supported via user impersonation + token exchange (§6). The
  `subject_token` is a Management-API-minted subject token, not the live session token.
- **GHCR namespace:** `ghcr.io/davamix/<app>`; each app publishes from its own CI (packages are
  private by default — make them public or `docker login ghcr.io` on the host).
- **Domains:** local-only for now — ecosystem-neutral `*.localhost` hosts (`auth.localhost`,
  `calendar.localhost`, `kanban.localhost`); real domains deferred. The standalone→integrated
  re-pointing rule now lives in the standards (architecture.md → *Deployment modes & joining the
  shared stack*, v0.4.0).

## 10 · Reference patterns in the Calendar repo (to copy)

When the Calendar repo is available, these are the patterns to mirror in Kanban — same shapes, new
names:

- Dual-mode compose with a `standalone` profile gating bundled `db`/`logto`/`caddy`:
  `docker-compose.yml`.
- Least-privilege DB role + init scripts: `db/init/`, `docs/security/postgres-least-privilege.md`.
- Dual-scheme auth (BFF cookie + JWT bearer), identity from `iss`+`sub`, owner/assignee model:
  `docs/auth.md`, `src/CalendarApi/Auth/`, `src/CalendarApi/Services/ICurrentUser.cs`.
- Reverse-proxy rationale and host routing: `docs/decisions/0002-reverse-proxy-caddy.md`, `Caddyfile`.
- ASVS L2 tracker structure: `docs/security/asvs-l2/`.
- Reviewer agents: `.claude/agents/`.
