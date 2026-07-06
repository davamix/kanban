# ADR 0005 — Project deletion (drag-to-delete + name verification)

- **Status:** Accepted (2026-07-06)
- **Related:** [0003-project-domain.md](0003-project-domain.md), [0004-project-creation-form.md](0004-project-creation-form.md), [../auth.md](../auth.md), [../STYLEGUIDE.md](../STYLEGUIDE.md), [../security/asvs-l2/V08-authorization.md](../security/asvs-l2/V08-authorization.md)

## Context

ADR 0004 shipped project **create** and left edit/delete deferred. This increment adds the first
owner-gated **mutation that destroys data** — project deletion — from the Stitch *Kanban Dashboard*
screens *"Project Selection - Drag to Delete Refinement"* and *"Project Deletion - Name Verification
Modal"*. It is the first concrete implementation of the owner-only mutation path that V8.3.1 has been
holding open since ADR 0003.

## Decisions

1. **`DELETE /api/projects/{id}` deletes a project; owner-only.** The store looks the project up
   through the existing global query filter (so a caller who can't see it gets `404`, never a `403`
   that would confirm it exists), then re-checks `OwnerId == ICurrentUser.Id` before removing. An
   **assignee** who can see the project but does not own it gets `403`. Success is `204 No Content`.
   The id comes from the route; the subject is always the session, never a payload field (ASVS V8).
2. **Three outcomes, one problem shape.** `IProjectStore.DeleteAsync` returns a `ProjectDeleteOutcome`
   enum (`Deleted` / `Forbidden` / `NotFound`) that the endpoint maps to `204` / `403` / `404`, with
   `403`/`404` emitted as RFC 9457 problem details via `Results.Problem` (consistent with the rest of
   the API). Keeping the HTTP mapping at the edge keeps the store transport-agnostic.
3. **Assignee rows cascade with the project.** No extra cleanup code: the `project_assignees`
   → `projects` FK is already `OnDelete: Cascade`, so removing the aggregate root removes its
   membership rows. No schema change, hence **no migration**.
4. **The UI gates the destructive action twice.** On the selection screen the left nav is removed and
   a **drop zone** sits fixed at the bottom — greyed and inert until a drag begins, then switched to
   the error colour and made droppable. Only **owner** cards are `draggable` (an assignee can't delete
   a shared project, so the affordance isn't offered). Dropping a card opens a **name-verification
   modal**: the user must type the project name exactly before the Delete button enables. The dialog
   title/description are cosmetic and name the target project.
5. **Frontend stays vanilla.** Drag-and-drop and the confirm modal are hand-written HTML/CSS/JS on
   `tokens.css` (no Tailwind/Node); the modal reuses the create-modal's focus-trap / Escape / backdrop
   behaviour. The drop zone is **desktop-only** — native HTML5 drag-and-drop isn't a touch gesture,
   and the zone would collide with the mobile bottom nav.

## Why

- **404-not-403 for invisible projects:** the query-filter lookup means the "can I see it?" and "can I
  delete it?" checks share one trusted path, and a non-owner can't use the delete response to probe
  which project ids exist. The explicit owner check then separates *read* access (owner **or**
  assignee) from *delete* access (owner **only**).
- **Cascade over manual delete:** relying on the FK that already models the relationship avoids a
  second code path that could drift from the schema.
- **Type-the-name confirmation:** deletion is permanent and takes the project's tasks with it; a
  friction step proportional to the blast radius (matching the Stitch design) guards against an
  accidental drag-and-drop.
- **Owner-only draggability:** enforcing the same rule client-side that the server enforces keeps the
  UI honest — the server is still the authority (an assignee's forged `DELETE` returns `403`).

## Consequences

- New endpoint `DELETE /api/projects/{id}`; `IProjectStore` gains `DeleteAsync` + the
  `ProjectDeleteOutcome` enum. No new column or migration.
- Antiforgery already covers `DELETE` on the cookie/BFF path (`IsUnsafe` includes it); the SPA sends
  the `X-CSRF-TOKEN` via the existing `csrfHeaders()` helper.
- New integration tests (`DeleteProjectEndpointTests`) pin the authorization boundaries: owner → `204`
  (row gone, off the list), assignee → `403` (survives), stranger → `404` (survives, no existence
  leak), unknown id → `404`, anonymous → `401`. This flips ASVS **V8.3.1** from ⏳ to ✅.
- The selection screen loses its left navigation panel (the nav links were placeholders); primary
  navigation on mobile is unchanged (bottom nav).
- ~~**Known limitation (inherited from the Stitch design): delete is pointer-only.**~~
  **Resolved (2026-07-06, follow-up).** The initial implementation reached delete only via native
  HTML5 drag-and-drop, leaving keyboard and screen-reader users with no path to it. The Stitch screen
  *"Project Selection - Accessible Drag-and-Drop Refinement"* adds an **accessible delete affordance**:
  each owner card is now a labelled `role="article"` (spoken `aria-label`: name · ownership · dates)
  with two keyboard/AT paths to the same name-verification confirm modal, and the drop zone is a
  labelled `role="region"`:
  - **Press `Delete` (or `Backspace`) while a card is focused** — the primary keyboard path,
    advertised to screen readers via `aria-keyshortcuts="Delete"`. `Enter` is deliberately reserved
    for **opening the project** (a later screen), so it is not bound to delete.
  - **A per-card Delete button that is screen-reader-only until focused**, then revealed at the card's
    top-right — the click / screen-reader affordance (activated with Enter/Space *on the button*).

  Drag-and-drop remains the pointer path; all three share one confirm modal.
- Still deferred: project **edit** + assignee management, Calendar mirroring on delete, and the
  **board** screen.
