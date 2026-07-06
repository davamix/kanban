# ADR 0006 — Project editing (hover Edit panel + reused project form)

- **Status:** Accepted (2026-07-06)
- **Related:** [0003-project-domain.md](0003-project-domain.md), [0004-project-creation-form.md](0004-project-creation-form.md), [0005-project-deletion.md](0005-project-deletion.md), [../auth.md](../auth.md), [../STYLEGUIDE.md](../STYLEGUIDE.md), [../security/asvs-l2/V08-authorization.md](../security/asvs-l2/V08-authorization.md)

## Context

ADR 0004 shipped project **create** and ADR 0005 shipped **delete**, both owner-gated; **edit** was
the remaining deferred mutation on the selection screen. This increment adds it. There is no Stitch
prototype for the edit affordance, so it reuses the existing design system (`tokens.css`) and the
create form's markup verbatim.

## Decisions

1. **`PUT /api/projects/{id}` replaces a project's editable fields + assignees; owner-only.** Like
   delete, the store looks the project up through the global query filter (an invisible id is a `404`,
   never a `403` that would confirm existence), then re-checks `OwnerId == ICurrentUser.Id` before
   mutating. An **assignee** who can see the project but does not own it gets `403`. Success is
   `200 OK` with the refreshed `ProjectResponse` so the SPA re-renders the card without a follow-up
   fetch. The id comes from the route and the subject from the session — never a payload field
   (ASVS V8); the owner is immutable and is never taken from the request.
2. **Three outcomes, one problem shape.** `IProjectStore.UpdateAsync` returns a `ProjectUpdateResult`
   (a `ProjectUpdateOutcome` enum — `Updated` / `Forbidden` / `NotFound` — plus the read model on
   success) that the endpoint maps to `200` / `403` / `404`, with `403`/`404` emitted as RFC 9457
   problem details. This mirrors the delete shape and keeps the store transport-agnostic.
3. **Full-replace semantics + assignee reconciliation.** `PUT` carries the whole editable project
   (same shape as create — a new `UpdateProjectRequest` record identical to `CreateProjectRequest`).
   Scalar fields are overwritten; the assignee set is **reconciled** (drop rows no longer selected —
   the required FK deletes the join row — add newly-selected ones). Requested ids are validated
   against the Logto directory (unknown ids dropped) and the **owner is always kept** among them, so
   an edit can never orphan the project from its owner. New assignees are mirrored into the local
   `users` table exactly as create does.
4. **Shape validation is shared by create and edit.** The identical field rules (name required/≤200,
   description ≤2000, both dates required, end ≥ start, budget ≥ 0) moved into one
   `ValidateProject(...)` helper called by both `POST` and `PUT`, returning the same camelCase-keyed
   problem-details errors the SPA already maps to inputs.
5. **The card carries a hover/focus Edit panel; the create modal is reused for editing.** Because
   only the owner can edit, only **owner** cards get the affordance. A `card-actions` tray reveals
   below the card on hover / card focus / when its Edit button is tabbed to (`:focus-within`),
   absolutely positioned so revealing it never reflows the grid and `pointer-events: none` while
   dormant so it can't block the card beneath. The same modal from ADR 0004 now drives both modes:
   `app.js` swaps the title / subtitle / submit label and seeds the assignee chips (create → the
   current user; edit → the project's assignees), then `POST`s or `PUT`s accordingly.
6. **Card keyboard model, completed.** With a full set of card actions now defined, each is mirrored
   for keyboard / assistive tech and advertised via `aria-keyshortcuts`, fired only when the card
   itself (not an in-card button) holds focus:
   - **`Enter` / click → open the task board** (still a "coming soon" placeholder — the board screen
     is a later increment). This is the primary action, so it keeps the plain click/Enter.
   - **`Space` → open the edit form** (owner-only; the key is prevented from scrolling the page).
   - **`Delete` / `Backspace` → open the delete-confirm dialog** (owner-only; unchanged from ADR 0005).

## Why

- **`PUT` full-replace over `PATCH`:** the edit form already presents and submits every field, so a
  full representation is the honest contract and avoids partial-update merge ambiguity. It reuses the
  create form's validation and payload shape unchanged.
- **404-not-403 for invisible projects:** same trusted lookup path as delete — the "can I see it?"
  and "can I edit it?" checks share the query filter, so a non-owner can't probe project ids, and the
  explicit owner check separates *read* access (owner **or** assignee) from *edit* access (owner
  **only**).
- **Owner always kept in the assignee set:** reconciliation could otherwise drop the owner if the
  client omitted them; forcing the owner in server-side keeps the invariant (owner ⊆ assignees) that
  create established, independent of what the client sends.
- **Reusing the create modal:** one form, one focus-trap/Escape/backdrop implementation, one set of
  field-error wiring — less surface to drift, and edit inherits the create screen's accessibility.
- **`Space` for edit, `Enter` for the board:** the board is the card's primary destination, so it
  keeps the natural click/Enter; edit is a secondary action reached by hover (pointer) or `Space`
  (keyboard), keeping the two from colliding.

## Consequences

- New endpoint `PUT /api/projects/{id}`; `IProjectStore` gains `UpdateAsync` + the
  `ProjectUpdateResult` record and `ProjectUpdateOutcome` enum; a new `UpdateProjectRequest` model.
  No new column or migration — editing only writes existing fields and the existing join table.
- Create-endpoint validation is refactored into the shared `ValidateProject` helper (behaviour
  unchanged; create tests still pass).
- Antiforgery already covers `PUT` on the cookie/BFF path (`IsUnsafe` includes it); the SPA sends
  `X-CSRF-TOKEN` via the existing `csrfHeaders()` helper.
- The create modal is generalised into a create/**edit** project form (`openProjectModal(project?)`,
  `submitProjectForm`); the DOM ids are unchanged, and a `#createSubtitle` id was added so the
  subtitle can be swapped per mode.
- New integration tests (`UpdateProjectEndpointTests`) pin the authorization boundaries and
  behaviour: owner → `200` (fields + assignees change), assignee → `403` (unchanged), stranger →
  `404` (no existence leak, unchanged), unknown id → `404`, anonymous → `401`, invalid shape → `400`
  problem details, and the owner is never dropped from the assignee set. Reinforces ASVS **V8.3.1**.
- Still deferred: assignee **role** management beyond membership, Calendar mirroring on edit, and the
  **board** screen (Enter/click still shows a "coming soon" toast).
