# ADR 0007 — Task board (columns + tasks)

- **Status:** Accepted (2026-07-07)
- **Related:** [0003-project-domain.md](0003-project-domain.md), [0004-project-creation-form.md](0004-project-creation-form.md), [0006-project-editing.md](0006-project-editing.md), [../auth.md](../auth.md), [../security/asvs-l2/V08-authorization.md](../security/asvs-l2/V08-authorization.md)
- **Stitch:** "Kanban Dashboard - Drag to Delete Refinement" + "Task Creation Form - Final Harmonization" (project `12395060896990119837`).

## Context

The project-selection screen (ADRs 0003–0006) was the first surface; clicking a project card only
raised a "coming soon" toast. This increment builds the **task board** behind that card: a project's
work, organised into workflow columns, with draggable task cards. It is the first domain beyond the
project itself, and the first place the owner/assignee split has two *different* answers for two
resource kinds on the same project.

## Decisions

1. **Two new entities: `BoardColumn` and `TaskItem`.** A column belongs to a project and carries a
   `Position`; the ordered set of a project's columns **is** its workflow (`TODO → WIP → DONE` by
   default). A task belongs to a column, and **its column is its status** — there is no separate
   status enum. `TaskItem` (not `Task`, to avoid `System.Threading.Tasks.Task`) carries title,
   description, `TaskPriority` (`Low`/`Medium`/`High`/`Urgent`, default `Medium`, stored as text), a
   single optional assignee, an optional due date, free-text `Labels` (Postgres `text[]`), and a
   `Position` within its column. Tasks also denormalise `ProjectId` so the visibility filter and the
   assignee-restriction check don't have to join through the column.

2. **Two authorization tiers on the same project.** Columns are *structural* — add / rename /
   reorder / delete are **owner-only** (`EfBoardStore` re-checks `OwnerId` after the query-filter
   lookup, exactly like project edit/delete: owner → ok, visible-but-not-owner → `403`, not-visible →
   `404`). Tasks are *collaborative* — create / edit / move / delete are open to **any project
   member** (owner or assignee); there membership **equals** visibility, so there is no `403` for
   tasks (a member who can see the project can act; a non-member gets `404`, no existence leak).

3. **Visibility inherits from the project, at the data layer.** New global query filters on
   `BoardColumn` and `TaskItem` (keyed off the same `ICurrentUser` as `Project`) restrict both to the
   owner-or-assignee set and **fail closed** when no subject is set (ASVS V8.4.1). Because EF requires
   a filtered entity to reference only filtered entities, the two are written explicitly rather than
   navigating into `Project`'s filter.

4. **A task's assignee is restricted to the project's assignees.** Enforced server-side in the store
   (an out-of-set id → `400` with an `assigneeId` field error), independent of what the picker offers.
   The board read model returns the project's assignees so the form's picker can be limited to them
   without a second `/api/users` call.

5. **Deleting a non-empty column is refused (`409`).** No silent data loss — the owner must move or
   delete the column's tasks first, and the SPA **disables the delete control** (with an explaining
   tooltip) while the column has tasks, so the refusal is communicated up front rather than as a
   post-confirm error; the `409` remains the server-side backstop. The task↔column FK is `NoAction`
   (so a direct non-empty-column delete is also blocked at the DB, checked at end-of-statement); the
   task↔project FK is `Cascade`, so deleting a *project* still clears its columns and tasks in one
   statement without the FK check aborting the cascade.

6. **Nested REST surface + one board read.** `GET /api/projects/{id}/board` returns the heading,
   `isOwner`, the project's assignees, and ordered columns each with their ordered tasks — everything
   the screen needs in one call. Mutations: `POST/PUT/DELETE …/columns[/{id}]`, `PUT …/columns/order`
   (reorder), `POST/PUT/DELETE …/tasks[/{id}]`, `PUT …/tasks/{id}/move` (drag). Errors are RFC 9457
   problem details; semantic `400`s are keyed to the payload field so the SPA surfaces them inline.

7. **Default columns are seeded on project create + lazily on first board read.** New projects get
   `TODO → WIP → DONE` in `EfProjectStore.CreateAsync`; projects created before this change are
   backfilled the first time their board is requested. So every board opens onto a usable workflow.

8. **Positions are server-authoritative and kept contiguous.** Move reindexes the target column (and
   closes the gap in the source); delete reindexes the column. The SPA never reconciles positions by
   hand — every mutation re-fetches the board.

9. **Frontend: a dedicated `board.html` + `board.js`, sharing helpers via `common.js`.** A card click
   navigates to `board.html?project={id}`. The reusable helpers (`getCookie`, `csrfHeaders`,
   `guardAuth`, `escapeHtml`, `initials`, date/toast/dialog utilities) were extracted from `app.js`
   into `common.js`, loaded by both pages. Task cards use native HTML5 drag-and-drop to move between
   columns; **column reordering uses accessible move-left/right buttons** (owner-only) rather than
   drag, for a keyboard/AT path and to avoid drag-type collisions with task cards. Attachments are an
   **inert** control (field present, no domain/endpoint), matching how "Client/Organization" was
   handled in ADR 0004.

## Why

- **Column-as-status (no status enum):** the workflow order and the set of statuses are the same
  thing; storing a status enum alongside the column would let them drift. Reordering columns
  therefore reorders the workflow, exactly as the brief states.
- **Owner-only columns, member tasks:** the workflow *shape* is a project-structure decision (the
  owner's), while filling the board with work is the collaboration the assignees are there for. This
  keeps the structural surface small and the collaborative surface open.
- **Refuse (not cascade) on non-empty column delete:** the product owner's call — deleting a column
  should never quietly take tasks with it.
- **Buttons for column reorder, DnD for tasks:** task movement is the core kanban gesture and worth
  native DnD; column reorder is rare and structural, and buttons give it a first-class keyboard/AT
  path without the pointer-only fragility (and no need to disambiguate task vs. column drags).
- **Re-fetch after every mutation:** the board is small; a re-fetch is far less bug-prone than
  hand-reconciling positions across columns in the client.

## Consequences

- New models (`BoardColumn`, `TaskItem`, `TaskPriority`), board DTOs/result records, `IBoardStore` +
  `EfBoardStore`, `BoardEndpoints`, and one EF migration (`AddBoardColumnsAndTasks`: `board_columns`
  + `tasks`, with `(ProjectId, Position)` / `(ColumnId, Position)` / `AssigneeId` indexes and the
  Restrict/Cascade/SetNull FKs above). `EfProjectStore.CreateAsync` now also seeds default columns.
- New frontend files `common.js`, `board.html`, `board.js` + board styles in `styles.css`; `app.js`
  slimmed (helpers moved to `common.js`) and its card entry point now navigates to the board.
- New `BoardEndpointTests` (Testcontainers) pin: anonymous → `401`, default columns, stranger → `404`
  (no leak), assignee-as-member reads/writes tasks, the assignee restriction, task move/reindex,
  owner-only column CRUD + reorder (assignee → `403`), non-empty column delete → `409`, and invalid
  reorder → `400`. Reinforces ASVS **V8.2.2 / V8.3.1 / V8.4.1**.
- Antiforgery already covers `POST/PUT/DELETE` on the cookie/BFF path; the SPA sends `X-CSRF-TOKEN`
  via the shared `csrfHeaders()` helper.
- Still deferred: **WIP / column limits** (the mock's "AT CAPACITY" hint is intentionally not built),
  task comments/activity, real **attachments**, assignee **role** management, and the Calendar mirror
  (projects only, and still deferred).
