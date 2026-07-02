# Authentication & authorization

How Kanban authenticates users, authorizes access to projects/tasks, and integrates with Logto.
Decisions and rationale live in [decisions/0001-infrastructure-bootstrap.md](decisions/0001-infrastructure-bootstrap.md);
this is the *how*. Security requirements are tracked in [security/asvs-l2/](security/asvs-l2/).

> **Infra-phase status:** the dual-scheme auth (BFF cookie + JWT bearer), user mirroring, the
> assignee directory, antiforgery and rate limiting are wired now. The **owner/assignee query
> filter and owner checks** described under *Authorization* land with the domain entities in the
> implementation phase.

## Access model

Projects and tasks each have an **owner** (its creator) and a set of **assignees**. The creator
is auto-added as an assignee, so the read rule is uniform. A task's assignee must be one of the
parent project's assignees.

| Action | Allowed for |
|---|---|
| Create | any authenticated user |
| See / search | owner + assignees |
| Edit / delete | **owner only** |
| Add / remove assignees | **owner only** |

Identity for "owner" / "assignee" is the Logto `sub`, derived from the validated session —
**never** from the request body.

## Identity (Logto)

- **Hosted sign-in/sign-up.** When there is no active session the app redirects to Logto's
  hosted page. Kanban has no custom login/registration UI. The sign-in method (username+password,
  passwordless, …) is a Logto *sign-in experience* console setting, not app code.
- **Users are mirrored locally.** A lightweight `users` table keyed by the Logto `sub`
  (`Id` = sub, `Email`, `DisplayName`) is upserted on first login and whenever a user is
  referenced as an assignee. Owner/assignee columns are FKs to it. Users are identified by
  `iss`+`sub`, which Logto never reassigns.
- **Assignee directory.** The assignee picker lists Logto users via the **Management API**
  (M2M client-credentials). v1 lists all users; a future Logto **Organization** will scope it.

## Authentication — dual scheme

`/api/*` accepts **either** of two schemes (default authorization policy lists both):

1. **Cookie (BFF)** — for the browser. `GET /login` issues an `OpenIdConnect` challenge
   (code flow, PKCE) to Logto; on callback the host creates an encrypted HttpOnly cookie and
   keeps the tokens server-side (`SaveTokens=true`). **Tokens never reach JavaScript**
   (ASVS V10.1.1). The OIDC request includes `resource={Logto:Audience}` (RFC 8707) so the
   access token is a JWT for the Kanban API resource.
2. **JWT bearer** — for machine / inter-app callers. Validates signature via the Logto JWKS
   (`Authority` pinned to the issuer, `RequireHttpsMetadata`), `aud` == `Logto:Audience`,
   issuer, and lifetime; `alg:none` rejected; `MapInboundClaims=false` so `sub` is read raw.

`ICurrentUser` (scoped) resolves `sub`/email/name from `HttpContext.User` regardless of the
scheme — the single place identity is derived.

The BFF is **only wired when a Logto web client is configured** (`LOGTO__WEB__CLIENTID`). Until
then the app still boots as a JWT resource server, so it runs cleanly before the one-time Logto
console setup.

### Logout

`/logout` is an **antiforgery-protected POST** (not a GET — avoids the CSRF / forced-logout
vector). It signs out the local cookie, then redirects to Logto's `end_session_endpoint` with
a **registered** `post_logout_redirect_uri`, behind an open-redirect guard. The antiforgery
token is delivered to the SPA (cookie) so the logout form/header can submit it.

## Authorization — two trusted layers (ASVS V8)

_(Layer 1 is implemented + tested for `Project`; layer 2 arrives with the project-creation
screen's mutating endpoints. See [decisions/0003-project-domain.md](decisions/0003-project-domain.md).)_

1. **EF Core global query filter** on the domain entities (`Project`, and `Task` later), keyed off
   `ICurrentUser` injected into `KanbanDbContext`:
   `e.OwnerId == current || e.Assignees.Any(a => a.UserId == current)`.
   All reads are isolated at the DB layer → IDOR/BOLA blocked. An unset current user matches no
   row (**fail closed**). Legitimate bypasses (seeding) use `IgnoreQueryFilters()` with a
   `// reason:` comment.
2. **Owner check** in mutate handlers (PUT/DELETE/assign): a *visible-but-not-owner* request
   → `403`; a *not-visible* one → `404` (the filter already hid it).

## Logto registration manifest (console checklist)

Provisioned once per environment in the Logto admin console (bundled in standalone mode).
Record the resulting IDs in `.env` — see [deployment.md](deployment.md) and `.env.example`.
The full ecosystem object list (incl. the Kanban→Calendar token-exchange caller) is in
[ecosystem-integration.md](ecosystem-integration.md) §7.

1. **API Resource** for the REST API. Indicator == `LOGTO__AUDIENCE` (`https://kanban.api`).
   Enable `offline_access`.
2. **Application → Traditional Web App** for the BFF.
   - **Redirect URI:** `http://<host>/signin-oidc` (ASP.NET's `CallbackPath`).
   - **Post sign-out redirect URI:** `http://<host>/signout-callback-oidc` — ASP.NET's
     `SignedOutCallbackPath`, the value the app actually sends to Logto. **Registering only
     `/` causes "post_logout_redirect_uri not registered" on logout.**
   - Copy `ClientId`/`ClientSecret` → `LOGTO__WEB__CLIENTID` / `LOGTO__WEB__CLIENTSECRET`.
3. **Application → Machine-to-Machine** for the user directory. **Roles → assign the built-in
   "Logto Management API access" role** — without it the token request is rejected
   (`invalid_target`) and the assignee directory is empty. Copy creds →
   `LOGTO__MANAGEMENT__CLIENTID` / `…CLIENTSECRET`; set `LOGTO__MANAGEMENT__ENDPOINT` and
   `LOGTO__MANAGEMENT__RESOURCE=https://default.logto.app/api`.
4. **Kanban→Calendar caller** (deferred to the integration milestone) — an M2M with Management
   API access to mint impersonation subject tokens, plus a confidential client with **"Allow
   token exchange"** enabled and the **Calendar API resource** (`https://calendar.api`) granted.
   See [ecosystem-integration.md](ecosystem-integration.md) §6–§7.

**Gotchas (from the Acopio reference):**
- The M2M token request must ask for `scope=all`, or every Management API call returns
  `403 auth.forbidden`. This is in code, not config.
- Do **not** change `LOGTO__MANAGEMENT__RESOURCE` away from `https://default.logto.app/api`.
- Without the RFC 8707 `resource=` indicator, Logto issues an **opaque** token the API can't
  validate as a JWT.

## Environment contract

| Variable | Purpose |
|---|---|
| `LOGTO__ISSUER` | OIDC issuer URL (incl. `/oidc/`); `Authority` for both schemes |
| `LOGTO__AUDIENCE` | Kanban API resource indicator (`https://kanban.api`); validated `aud` |
| `LOGTO__WEB__CLIENTID` / `LOGTO__WEB__CLIENTSECRET` | BFF (Traditional Web App) client |
| `LOGTO__MANAGEMENT__ENDPOINT` / `…CLIENTID` / `…CLIENTSECRET` / `…RESOURCE` | M2M Management API client (assignee directory) |

Secrets come from the environment only — never source (see [.standards/security.md](../.standards/security.md)).
`.env.example` documents the names with no values.

## Enforcement points (files)

- Auth wiring, schemes, antiforgery, rate limiting — [AuthenticationExtensions.cs](../src/KanbanApi/Auth/AuthenticationExtensions.cs), [Program.cs](../src/KanbanApi/Program.cs)
- Identity abstraction — [ICurrentUser.cs](../src/KanbanApi/Services/ICurrentUser.cs)
- BFF endpoints (`/login`, `/logout`, `/api/me`) — [AuthEndpoints.cs](../src/KanbanApi/Endpoints/AuthEndpoints.cs)
- User directory — [LogtoManagementClient.cs](../src/KanbanApi/Services/LogtoManagementClient.cs)
- Global query filter + owner checks — added with the domain in [KanbanDbContext.cs](../src/KanbanApi/Data/KanbanDbContext.cs)
