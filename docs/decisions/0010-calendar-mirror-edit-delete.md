# ADR 0010 — Kanban → Calendar project mirror: edit & delete propagation

- **Status:** Accepted (2026-07-10)
- **Related:** [0009-calendar-mirror.md](0009-calendar-mirror.md),
  [0005-project-deletion.md](0005-project-deletion.md),
  [0006-project-editing.md](0006-project-editing.md),
  [../ecosystem-integration.md](../ecosystem-integration.md) §6–7,
  [../security/asvs-l2/V10-oauth-and-oidc.md](../security/asvs-l2/V10-oauth-and-oidc.md)

## Context

[ADR 0009](0009-calendar-mirror.md) shipped the Kanban → Calendar project mirror **create-only**: when a
user creates a Kanban project, a matching Calendar project appears, owned by the same user (via RFC 8693
on-behalf-of). Editing or deleting the Kanban project did **not** propagate, so the two apps drift — a
renamed/re-dated project keeps its create-time values in Calendar, and a deleted Kanban project leaves an
orphan Calendar project. ADR 0009 called this out as the most likely first surprise. This ADR closes the
gap by propagating edits and deletes over the same on-behalf-of path.

`EfProjectStore.UpdateAsync` / `DeleteAsync` and their `PUT` / `DELETE /api/projects/{id}` endpoints
already existed (ADR 0004–0006) and enforce owner-only authorization locally; they simply made no
Calendar call. Calendar exposes the needed operations (verified against its `/openapi/v1.json` on
2026-07-10): `PUT /api/projects/{id}`, `DELETE /api/projects/{id}`, and
`DELETE /api/projects/{id}/assignees/{userId}` alongside the create/assignee-add endpoints — all
owner-only, all returning `200`.

## Decisions

1. **Edit = full sync.** On an owner edit, propagate the scalar fields (name/description/dates) via
   `PUT /api/projects/{calendarId}` **and** reconcile the assignee set: `DELETE` each removed and `POST`
   each added assignee. The delta is computed from the store's before/after assignee sets, **excluding
   the owner** (Calendar auto-manages the owner). Scalar `PUT` failure ⇒ `Failed`; a per-assignee failure
   only logs (the scalar update already landed) ⇒ still `Mirrored`, matching create's posture.

2. **Delete = best-effort propagation.** On an owner delete, `DELETE /api/projects/{calendarId}` runs
   *before* the local removal (while the entity's `CalendarProjectId` is still in hand). The Kanban delete
   proceeds regardless of Calendar's outcome; a `404` counts as already-gone. A failure just logs and
   leaves a Calendar orphan — never blocks the Kanban delete.

3. **Only propagate for already-mirrored projects.** Edit and delete propagate **only** when the project
   has a `CalendarProjectId` (i.e. `MirrorStatus = Mirrored`). A `Skipped`/`Failed` project has no
   Calendar counterpart, so an edit/delete is a no-op toward Calendar. Create and edit responsibilities
   stay separate — an edit does **not** back-fill/create a missing counterpart; retrying a `Failed`
   mirror remains a distinct deferred feature.

4. **Refresh `MirrorStatus` on edit.** A successful edit re-affirms `Mirrored`; a failed propagation flips
   the project to `Failed`, flagging the drift (the `CalendarProjectId` mapping is kept). This is a
   second best-effort save, belt-and-braces guarded so it can never fail the edit.

5. **Same on-behalf-of path, same never-throws/inline/10s-deadline posture as create.** Two new
   `ICalendarMirror` methods — `UpdateProjectAsync(project, added, removed)` and
   `DeleteProjectAsync(project)` — reuse `ILogtoTokenExchange` for the owner's `sub`, each under its own
   10s linked-CTS deadline, wrapped so no error escapes. A shared `TryPrepareAsync` helper (config gate +
   token) removes the duplication across create/edit/delete.

6. **No new configuration, no schema change.** Reuses `LOGTO__EXCHANGE__*`, `CALENDAR__API__BASEURL`,
   `CALENDAR__API__RESOURCE` and the existing 10s typed `HttpClient`. Unset ⇒ the new methods degrade to
   `Skipped`/no-op, same as create.

## Why

- **Identity stays the owner's `sub` from the entity, never client input.** Both new methods exchange for
  `project.OwnerId`, so the Calendar owner/actor can never be steered by a request payload (ASVS V10.2,
  V8) — the same guarantee create already has. No new auth surface, so no new ASVS row.
- **Best-effort over transactional.** A Calendar/Logto outage must not block a user editing or deleting
  their own Kanban project; recording drift (`Failed`) or accepting a transient orphan is the cheaper,
  reversible failure — consistent with ADR 0009's reasoning against a distributed transaction/outbox at
  this scale.
- **Delete-before-local-remove** keeps the `CalendarProjectId` available for the propagation call and
  avoids persisting a local delete we then can't act on.
- **Leave-not-yet-mirrored-as-is** keeps behaviour predictable (edit doesn't silently change mirror
  state) and avoids conflating the deferred retry/back-fill concern into the edit path.

## Consequences

- `ICalendarMirror` gains `UpdateProjectAsync` / `DeleteProjectAsync`; `CalendarMirror` factors a shared
  `TryPrepareAsync` (create refactored onto it). `EfProjectStore.UpdateAsync` / `DeleteAsync` gain the
  belt-and-braces mirror hooks. No `Program.cs` / DI change; no migration.
- **Drift on failure is now surfaced, not silent:** a failed edit propagation shows `Failed` on the card.
  A failed delete propagation is only logged (the project row is gone) — the Calendar orphan needs a
  future reconcile.
- Tests: `FakeCalendarMirror` records update/delete calls and their assignee delta; endpoint tests prove
  edit/delete still return 200/204 for mirrored, not-yet-mirrored, and failing-propagation cases;
  `RecordingHandler` unit tests cover the real `PUT` + assignee add/remove and `DELETE` (incl. 404
  already-gone and soft-fail). 91 integration tests green. No end-to-end run in the dev container — that
  lands in the integration milestone (the Calendar container used to verify the endpoints is available).
- **Still deferred:** retry/reconcile of a `Failed` mirror (now reachable via edit but not automatic),
  reconciling a Calendar orphan left by a failed delete, mirroring tasks, and the `actor_token` audit
  claim.
