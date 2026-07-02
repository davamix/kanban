# ADR 0003 — Project domain & the project-selection screen

- **Status:** Accepted (2026-07-02)
- **Related:** [0001-infrastructure-bootstrap.md](0001-infrastructure-bootstrap.md), [../auth.md](../auth.md), [security/asvs-l2/V08-authorization.md](security/asvs-l2/V08-authorization.md)

## Context

With the infrastructure and auth in place (ADR 0001) and Logto configured, the first product
increment is the **project-selection screen** — the app's entry point, listing the projects the
signed-in user owns or is assigned to (the Stitch *Kanban Dashboard → "Project Selection -
Streamlined Layout"* screen). This lands the first slice of the domain and the owner/assignee
authorization model the whole app depends on.

## Decisions

1. **`Project` is a first-class entity**, not a projection of Calendar's: `Id`, `Name`,
   `Description`, `StartDate`, `EndDate`, `OwnerId`, `CreatedAt/By`, and a `ProjectAssignee` join
   (composite key, cascade-delete). Board columns and tasks hang off a project in a later screen.
2. **Access model = owner + assignees** (as documented in [auth.md](../auth.md)). Read isolation is
   an EF Core **global query filter** on `Project` keyed off `ICurrentUser`
   (`OwnerId == me || Assignees.Any(a => a.UserId == me)`), which **fails closed** when no user is
   set. The creator is treated as an assignee, so "visible to me = I own it or I'm assigned".
3. **Read side only this increment.** `GET /api/projects` returns a `ProjectResponse` read model
   (id, name, description, dates, `isOwner`/`role`, resolved assignee names) — exactly what a card
   needs, with the caller's relationship resolved **server-side** so the client never infers access
   from raw ownership fields. Create/update/assignee management arrive with the project-creation
   screen; the create affordances advertise "coming soon".
4. **Populated by a Development-only per-user seeder** at first login (the filter is per-user, so a
   fixed seed owned by someone else would be invisible). Idempotent; never runs outside Development.
5. **Frontend stays vanilla** (hand-written CSS on `tokens.css`, no Tailwind/Node), mirroring
   Calendar. The Stitch mockup's Tailwind CDN + Material tokens are translated into our token set.
6. **Kanban takes its own brand primary but the shared flat aesthetic.** Per the *KanbanFlow*
   design-system doc the primary is **#2563EB** (Calendar uses #004ac6); everything else follows
   the Streamlined Layout screen, which is **flat and square** (0 radius, border-led, no shadows) —
   the same aesthetic as Calendar. Neutrals (Slate) and the Material-3 token naming are shared.
   Primary is the one deliberate divergence (`--color-primary`); `--radius` stays 0.

## Why

- **Own entity, not shared with Calendar:** the two domains only overlap on a few fields; coupling
  them would leak Calendar's shape into Kanban. Mirroring into Calendar happens later through
  Calendar's *API*, never a shared table (ADR 0001 §6).
- **Query filter as the isolation mechanism:** one enforcement point that fails closed beats
  per-endpoint `Where` clauses that can be forgotten — and it's provable with cross-user tests.
- **Read-only first:** the screen's job is to *show* projects; the creation form is a distinct Stitch
  screen. Shipping the read slice (with its authorization model + tests) de-risks everything after it.
- **Adapting the mockup:** the prototype shows task counts, "efficiency %", and status badges that
  don't exist in our domain yet (no tasks). Rather than fake them, cards render what a project has —
  name, description, date range, assignee avatars, and the caller's owner/shared role.

## Consequences

- New tables `projects` + `project_assignees` (migration `AddProjects`; FK indexes on `OwnerId`
  and the join's `UserId`; cascade on the join, restrict on owner).
- ASVS **V8 rows flip to implemented + tested** — the global filter, the anonymous-401 gate, and the
  cross-user forgery/visibility proofs live in `tests/KanbanApi.IntegrationTests`.
- The `design-reviewer` and `integration-test-author` agents are added now that a UI and a test
  suite exist (deferred in ADR 0001).
- Deferred to the next increments: project **create/edit** + assignee management (mirroring into
  Calendar via token exchange), and the **board** screen (columns + tasks + drag-to-restatus).
