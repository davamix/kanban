# ADR 0009 — Kanban → Calendar project mirror (RFC 8693 on-behalf-of)

- **Status:** Accepted (2026-07-10)
- **Related:** [0003-project-domain.md](0003-project-domain.md),
  [0004-project-creation-form.md](0004-project-creation-form.md),
  [../ecosystem-integration.md](../ecosystem-integration.md) §6–7,
  [../security/asvs-l2/V10-oauth-and-oidc.md](../security/asvs-l2/V10-oauth-and-oidc.md)

## Context

Kanban and Calendar are two apps in the same ecosystem sharing one Logto identity provider. The
agreed behaviour (ecosystem-integration.md §6): when a user creates a Kanban **project**, a matching
**Calendar project** — same name, description, dates, assignees — should appear on that user's
Calendar, **owned by the same user**. Tasks are never mirrored, and Calendar must not change (its
public REST API is the only integration surface).

Calendar sets a project's owner from the access token's `sub`, never the request body. So a plain
machine (client-credentials) token would make *Kanban* the owner and the project would never land on
the user's Calendar. Kanban therefore has to call Calendar with a token that carries the **user's**
`sub` but is scoped to Calendar's audience — obtained via Logto **user impersonation + token exchange
(RFC 8693)**. Logto cannot exchange a live session token directly; the exchange's `subject_token`
must be an impersonation **subject token** minted through the Management API (the same M2M capability
the assignee directory already uses).

## Decisions

1. **Create-only, best-effort, never blocks creation.** The mirror runs as a side effect of
   `POST /api/projects`, *after* the local project is saved. A mirror failure (Logto/Calendar down,
   misconfig, non-2xx) is logged and recorded on the project, but the Kanban project is always
   created and the endpoint still returns 201. Edit/delete propagation is deferred.

2. **Inline, short timeout.** Mirroring is awaited during the create request (10s `HttpClient`
   timeout) so the Calendar id is captured synchronously — no background worker for this scope.

3. **Config-gated off by default.** With no exchange client / Calendar base URL configured the mirror
   degrades to `Skipped`, so standalone dev and existing tests are unaffected. Turning it on is pure
   configuration (`LOGTO__EXCHANGE__CLIENTID/SECRET`, `CALENDAR__API__BASEURL`,
   `CALENDAR__API__RESOURCE`) — no code change, matching the standalone→integrated posture.

4. **Persist the outcome on the project.** New `Project.MirrorStatus`
   (`ProjectMirrorStatus { Skipped, Mirrored, Failed }`, stored as text like `TaskPriority`) and
   `Project.CalendarProjectId` (`string?`). Both are **server-owned** — set on create, never
   client-supplied — and surfaced on `ProjectResponse` so the SPA can show a small "sync failed" hint
   on the owner's card. Migration `AddProjectCalendarMirror` backfills existing rows to `Skipped`.

5. **Layered services, each fails soft (null):**
   - `ILogtoManagementClient.MintSubjectTokenAsync(userId)` — reuses the existing M2M token to
     `POST /api/subject-tokens`.
   - `ILogtoTokenExchange.GetOnBehalfOfTokenAsync(userId, resource)` — subject token → token exchange
     as the confidential exchange client; null when the exchange client is unset.
   - `ICalendarMirror.MirrorProjectAsync(project, assigneeIds)` — **never throws**; returns
     `(status, calendarProjectId)`. Creates the Calendar project then `POST`s each **non-owner**
     assignee (Calendar auto-adds the owner from the token `sub`).

6. **Identity is the owner's `sub` from the entity, never client input.** `CalendarMirror` exchanges
   for `project.OwnerId` (itself set from the session `sub` on create), so the on-behalf-of token —
   and thus the Calendar owner — can never be steered by a request payload (ASVS V10.2, V8).

## Why

- **On-behalf-of, not a service account:** the whole point is that the user, not Kanban, owns the
  Calendar project; only a token carrying the user's `sub` achieves that.
- **Best-effort/inline over a durable queue:** at this scale a failed mirror is a low-cost, retryable
  gap (status is recorded); a background worker/outbox is unjustified complexity for one create call.
  A Calendar/Logto outage degrading Kanban project creation would be the worse failure.
- **Config-gated:** the integrated stack (exchange client provisioned in Logto) doesn't exist in the
  dev container, so the feature must be inert until wired — same pattern as the directory client
  degrading to empty.
- **Reused the M2M client** for subject-token minting rather than a parallel token cache.

## Consequences

- New services `LogtoTokenExchange`, `CalendarMirror` (+ their `AddHttpClient` registrations);
  `MintSubjectTokenAsync` added to `ILogtoManagementClient`; `EfProjectStore` gains the mirror call
  (belt-and-braces guarded) and threads the two fields into its read models.
- Schema: two nullable/defaulted columns on `projects` (migration `AddProjectCalendarMirror`); the
  non-nullable `MirrorStatus` is backfilled to `Skipped` so existing rows round-trip.
- SPA: owner cards show a `sync_problem` (Failed) / muted `event_available` (Mirrored) icon; nothing
  for `Skipped`. Spoken into the card's `aria-label`.
- Tests: `FakeCalendarMirror` (outcome by project-name prefix) proves create still returns 201 for
  Mirrored/Failed/Skipped and persists the fields; `StubHandler` unit tests cover the real
  subject-token → exchange → Calendar sequence, the exchanged bearer, and soft-fail. No end-to-end
  run in the dev container — that lands in the integration milestone.
- **Deferred:** edit/delete propagation, retry of a `Failed` mirror, mirroring tasks, and passing the
  user's token as `actor_token` for an `act` audit claim.

## Operational notes (after a live integration test, 2026-07-10)

Verified end-to-end against a real Calendar instance sharing Kanban's Logto: creating a Kanban project
produced a matching Calendar project **owned by the same user** (`iss`/`aud`/`sub` all lined up), with
the mapped id stored and the card showing `Mirrored`. Things to keep in mind operationally:

- **Create-only ⇒ the two apps drift.** Renaming/re-dating/deleting a Kanban project does **not**
  propagate; the mirrored Calendar project keeps its create-time values until edit/delete propagation
  lands. This is the most likely first surprise in real use.
- **`Failed` is recorded but never retried.** A transient Calendar/Logto outage leaves a project
  `Failed` with no calendar id and no reconciliation — needs a manual re-create or a future retry path.
- **A partial mirror still reports `Mirrored`.** The Calendar project is created first; a later
  per-assignee POST failure only logs a warning, so a project can be `Mirrored` yet missing an assignee.
- **`CALENDAR__API__BASEURL` is a server-to-server host, not a browser one** — see
  [../ecosystem-integration.md](../ecosystem-integration.md) §6 "Configuration & hostnames".
- The local test pointed Calendar at Kanban's *app-scoped* Logto issuer as a stand-in; a true
  integrated stack uses one **ecosystem-neutral** issuer owned by the platform repo.
