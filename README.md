# Kanban

A multi-user **project & board manager**. Users create **projects** (name, description, start/end
dates, assignees) and split them into **tasks** that move across board columns (`TODO` → `WIP` →
`DONE`, plus custom columns); moving a task changes its status. It ships a web UI **and** a REST
API. Data is persisted in **PostgreSQL**, and users sign in via **Logto**.

> Stack: **.NET 10** (ASP.NET Core minimal APIs) + vanilla HTML/CSS/JS frontend + EF Core/PostgreSQL,
> packaged with **Docker**. Same shape as the ecosystem's **Calendar** app.

- **Ecosystem integration:** [docs/ecosystem-integration.md](docs/ecosystem-integration.md) — how
  Kanban joins the shared Logto/Postgres/proxy and mirrors projects into Calendar.
- **Auth & access model:** [docs/auth.md](docs/auth.md) — Logto (BFF cookie for the browser, JWT
  bearer for machines); each element has an **owner** (creator) and **assignees**.
- **Security baseline:** OWASP ASVS 5.0 L2, tracked in [docs/security/asvs-l2/](docs/security/asvs-l2/).
- **Deployment:** standalone or integrated — [docs/deployment.md](docs/deployment.md).
- **Decisions:** recorded as ADRs in [docs/decisions/](docs/decisions/).

> **Status: first screen shipped.** On top of the infrastructure + full Logto auth wiring (BFF
> cookie + JWT bearer, user directory, rate limiting, antiforgery), the **project-selection screen**
> is live: `Project` domain + owner/assignee isolation + `GET /api/projects` + the dashboard SPA
> (see [docs/decisions/0003-project-domain.md](docs/decisions/0003-project-domain.md)). Still to
> come: project **create/edit** (+ mirroring into Calendar) and the **board** (columns, tasks,
> drag-to-restatus).

---

## Concepts

| Entity    | REST resource (planned) | Meaning                                             |
|-----------|-------------------------|-----------------------------------------------------|
| `Project` | `/api/projects`         | A project with dates + assignees; owns tasks        |
| `Task`    | `/api/projects/{id}/tasks` | A unit of work with a status/column + assignee   |
| `Column`  | `/api/projects/{id}/columns` | A board lane; a task's column drives its status |

Projects and tasks each have an **owner** (creator) and **assignees**. A task's assignee is
restricted to that project's assignees. Identity is always the Logto `sub`, never client-supplied.

---

## Run with Docker (standalone — bundles Postgres + Logto + Caddy)

```bash
cp .env.example .env          # fill in the passwords (see docs/auth.md)
docker compose --profile standalone up --build
```

This starts PostgreSQL, Logto, the app, and a **Caddy** reverse proxy (the single entry point on
port **8080** — forward only that port). On first run, complete the one-time
[Logto console checklist](docs/auth.md#logto-registration-manifest-console-checklist) at the admin
console **http://admin.kanban.localhost:8080**, paste the resulting IDs into `.env`, and re-run
`up -d`. Then open **http://localhost:8080** — with no session you're redirected to Logto's hosted
sign-in/sign-up page (`http://auth.kanban.localhost:8080`).

See [docs/deployment.md](docs/deployment.md) for how the proxy makes the OIDC flow work end-to-end
and for integrated (shared-infra) mode.

## Run tests

```bash
dotnet test Kanban.slnx     # unit + integration (integration uses Testcontainers → needs Docker)
```

_(Test projects arrive with the implementation phase; see [docs/testing.md](docs/testing.md).)_

---

## REST API

Base URL: `http://localhost:8080`. **Every `/api/*` endpoint requires authentication** — the
browser via the BFF cookie, machine callers via a `Bearer` JWT (audience = the Kanban API
resource, `https://kanban.api`).

Currently available:

| Method | Route                       | Description                                  |
|--------|-----------------------------|----------------------------------------------|
| `GET`  | `/api/projects`             | Projects the user owns or is assigned to     |
| `GET`  | `/api/me`                   | The signed-in user                           |
| `GET`  | `/api/users`                | User directory for the assignee picker       |
| `GET`  | `/login`, `POST /logout`    | BFF sign-in / sign-out                       |
| `GET`  | `/health`, `/openapi/v1.json` | Health probe / OpenAPI document            |

Project create/edit + the task/board CRUD surface are added in the next screens.

---

## Project layout

```
.
├── Dockerfile                 # multi-stage build (SDK → aspnet runtime, non-root)
├── docker-compose.yml         # app + (standalone profile) bundled Postgres + Logto + Caddy
├── Caddyfile                  # reverse proxy: routes app + Logto behind one port
├── db/init/                   # least-privilege role + bundled Logto DB (fresh-volume init)
├── Kanban.slnx
├── .standards/                # shared ecosystem standards (git submodule, pinned)
├── docs/                      # ecosystem-integration, auth, deployment, decisions, security/asvs-l2
├── .claude/agents/            # security / access-control / architecture / migration reviewers
├── src/KanbanApi/
│   ├── Program.cs             # app wiring, DI, endpoint mapping, OpenAPI
│   ├── Auth/                  # dual-scheme auth + antiforgery
│   ├── Models/                # AppUser (domain entities added in implementation)
│   ├── Data/                  # EF Core DbContext + design-time factory
│   ├── Services/              # ICurrentUser, Logto Management client
│   ├── Endpoints/             # auth (/login, /logout, /api/me), users
│   ├── Migrations/            # EF Core migrations
│   └── wwwroot/               # placeholder SPA shell (board UI added in implementation)
└── tests/                     # added in the implementation phase
```
