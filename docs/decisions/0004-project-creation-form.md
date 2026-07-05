# ADR 0004 — Project-creation form (write side)

- **Status:** Accepted (2026-07-05)
- **Related:** [0003-project-domain.md](0003-project-domain.md), [../auth.md](../auth.md), [../STYLEGUIDE.md](../STYLEGUIDE.md), [../api-design.md](../../.standards/api-design.md)

## Context

ADR 0003 shipped the read side (the project-selection screen) and left create/update deferred. This
increment adds the **project-creation form** — the Stitch *Kanban Dashboard → "Project Creation Form
- Icon Clean-up"* screen — as a modal on the selection screen, plus the `POST /api/projects` write
path behind it. Calendar mirroring stays deferred; the form works fully **standalone**.

## Decisions

1. **`POST /api/projects` creates a project owned by the caller.** The owner is taken from the
   authenticated session (`ICurrentUser`), **never** the payload (ASVS V8), and is always added as an
   assignee so the read rule ("visible = own or assigned") holds uniformly. Returns `201 Created`
   with the same `ProjectResponse` read model the selection screen consumes.
2. **Assignees are any Logto user**, chosen from the directory the Management API already exposes
   (`GET /api/users`, ADR 0003's assignee picker). The create path resolves the requested ids against
   that directory: matches are mirrored into the local `users` table (FK target, names enriched for
   the cards); ids **not** in the directory are **dropped** rather than failing the request. v1 lists
   all users; a future Logto **Organization** will scope it (per [auth.md](../auth.md)).
3. **Budget is a real, currency-neutral field.** New nullable `Project.Budget` (`numeric(18,2)`,
   migration `AddProjectBudget`). Deliberately **no currency** is stored or assumed and the UI shows
   no symbol — a bare amount that can be read in whatever currency the org uses. Non-negative.
4. **"Client / Organization" is a placeholder control only.** It renders (disabled) to match the
   mockup but has no domain field and is not submitted — deferred until the concept is designed.
5. **Validation is shape-only, as RFC 9457 problem details.** The endpoint checks name (required,
   ≤200), description (≤2000), `endDate ≥ startDate`, and `budget ≥ 0`, returning
   `Results.ValidationProblem` (per-field `errors`). The SPA mirrors the same checks inline for fast
   feedback and maps the server's `errors` back onto the fields. CSRF is the existing
   `X-CSRF-TOKEN` antiforgery path (cookie callers only).
6. **Frontend stays vanilla.** The modal is hand-written HTML/CSS on `tokens.css` (no Tailwind/Node);
   the mockup's Tailwind classes are translated into the existing token-based components.

## Why

- **Owner from session, assignees validated against the directory:** keeps the write path from
  trusting client-supplied identity, and guarantees every assignee FK resolves to a real user.
  Dropping unknown ids (rather than 400-ing) means a stale/empty directory degrades to "owner only"
  instead of a dead form — which is exactly the standalone case before the M2M app is provisioned.
- **Budget as a stored field, currency-neutral:** the product owner wanted budget captured, but the
  ecosystem has no currency model; storing a bare decimal keeps it honest and avoids implying a
  currency we don't track.
- **Inert Client/Organization:** matching the mockup without inventing a domain concept keeps the ADR
  trail truthful about what's actually implemented.

## Consequences

- New column `projects.Budget` (migration `AddProjectBudget`; nullable — safe additive change).
- `IProjectStore.CreateAsync` joins the DbContext to the Logto directory (`ILogtoManagementClient`)
  for assignee resolution; `ProjectResponse` gains `Budget`.
- Create integration tests (`CreateProjectEndpointTests`) cover owner auto-assignment, budget
  persistence, directory resolution + unknown-id dropping, and both validation paths — using the
  existing `FakeLogtoManagementClient` / `NoopAntiforgery` fixtures.
- The **Development-only per-user seeder** (ADR 0003 §4) is **removed** now that projects can be
  created for real — a fresh user starts on the empty state and creates their own. The `users` table
  no longer carries synthetic `seed-*` teammates.
- Deferred: project **edit** + assignee management, the **Client/Organization** concept, Calendar
  mirroring on create (token exchange), and the **board** screen.
