# Kanban — project instructions

This project is part of an application ecosystem and follows shared specifications vendored
at [.standards/](.standards/) (a git submodule → https://github.com/davamix/standards,
pinned per project). For how this convention works, see
[.standards/working-with-standards.md](.standards/working-with-standards.md).

If `.standards/` is empty, hydrate it: `git submodule update --init --remote .standards`.

## Shared standards (imported)

@.standards/architecture.md
@.standards/security.md
@.standards/api-design.md

## Project-specific

- **Stack:** ASP.NET Core minimal APIs (.NET 10) serving a vanilla HTML/CSS/JS SPA as
  static files from `wwwroot`. No Blazor, no Swagger UI, no Node/Tailwind toolchain — keep
  it lean. Mirrors the ecosystem's Calendar app.
- **Layout:** API in [src/KanbanApi/](src/KanbanApi/) (Endpoints / Models / Services / Data);
  frontend in [src/KanbanApi/wwwroot/](src/KanbanApi/wwwroot/).
- **Domain:** projects (name, description, start/end dates, owner, assignees) split into tasks
  (name, description, assignee, status). A board shows tasks in status columns (`TODO`/`WIP`/`DONE`
  + custom); moving a task between columns changes its status. Task assignees are restricted to
  the project's assignees. **The domain is not implemented yet** — this repo is the infrastructure
  scaffold (see [docs/decisions/0001-infrastructure-bootstrap.md](docs/decisions/0001-infrastructure-bootstrap.md)).
- **Persistence:** PostgreSQL via EF Core (one database per app). The app connects as a
  dedicated least-privilege role — see [docs/security/postgres-least-privilege.md](docs/security/postgres-least-privilege.md).
- **Auth:** Logto (central IdP); the browser uses a **BFF cookie**, machine callers use a
  **JWT-bearer** scheme. Access model = owner + assignees. See [docs/auth.md](docs/auth.md).
- **Ecosystem integration:** Kanban mirrors each new project into **Calendar** via Calendar's REST
  API, acting **on behalf of the user** through Logto token exchange (RFC 8693). Kanban never
  touches Calendar's database. See [docs/ecosystem-integration.md](docs/ecosystem-integration.md).
- **Security baseline:** OWASP ASVS 5.0 L2, tracked in [docs/security/asvs-l2/](docs/security/asvs-l2/).
  Reviewer agents in [.claude/agents/](.claude/agents/) help close findings on each diff.
- **Deployment:** runs standalone (bundled infra) or integrated (shared ecosystem infra) —
  see [docs/deployment.md](docs/deployment.md).
- **Decision log:** every big change is recorded as an ADR in [docs/decisions/](docs/decisions/)
  (context → decision → why → consequences). Add one for the next big change.
- **Project-specific specs and backlog** live in [docs/](docs/), not in `.standards/`.
