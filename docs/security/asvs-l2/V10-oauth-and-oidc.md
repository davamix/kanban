# V10 — OAuth and OIDC

**ASVS 5.0 L2** · [← dashboard](README.md)

> **Roles:** Kanban is an **OIDC Relying Party** (the BFF cookie session) and an **OAuth
> Resource Server** (`/api/*` JWT-bearer). Logto is the Authorization Server / OpenID Provider,
> so V10.4 (AS) / V10.6 (OP) / V10.7 (consent) are largely Logto-side. When Kanban calls Calendar
> via RFC 8693 token exchange it is also an **OAuth client** performing on-behalf-of delegation —
> see [../../ecosystem-integration.md](../../ecosystem-integration.md) §6 (implemented later).

## Status summary

✅ Implemented — BFF (cookie/OIDC) + JWT-bearer schemes in
[AuthenticationExtensions.cs](../../../src/KanbanApi/Auth/AuthenticationExtensions.cs).

---

## V10.1 — Generic

| Req | L | State | Notes |
|-----|---|-------|-------|
| V10.1.1 | 2 | ✅ | **BFF**: access/refresh tokens live in the encrypted auth cookie (`SaveTokens=true`), never delivered to browser JS. |
| V10.1.2 | 2 | ✅ | ASP.NET OIDC middleware binds `state`/`nonce`/PKCE `code_verifier` per transaction in an encrypted correlation cookie. |

## V10.2 — OAuth client

| Req | L | State | Notes |
|-----|---|-------|-------|
| V10.2.1 | 2 | ✅ | PKCE (default in .NET 6+) **and** `state` validation against the correlation cookie. |
| V10.2.2 | 2 | ✅ | One AS (Logto); `Authority` pinned; `iss` validated. |

## V10.3 — Resource server

| Req | L | State | Notes |
|-----|---|-------|-------|
| V10.3.1 | 2 | ✅ | `ValidateAudience` (see [V09](V09-self-contained-tokens.md)). |
| V10.3.2 | 2 | ✅ | Authorization keyed on `sub` (owner/assignee). |
| V10.3.3 | 2 | ✅ | User identified by `iss`+`sub` (`MapInboundClaims=false` keeps raw `sub`); mirrored as `users.Id`. |

## V10.5 — OIDC client (RP)

| Req | L | State | Notes |
|-----|---|-------|-------|
| V10.5.1 | 2 | ✅ | `nonce` validated on the ID token (middleware default). |
| V10.5.2 | 2 | ✅ | User uniquely identified by `sub`. |
| V10.5.3 | 2 | ✅ | `Authority` issuer compared to discovery `issuer`; mismatch throws. |
| V10.5.4 | 2 | ✅ | ID-token `aud` validated against the BFF `ClientId`. |

## V10.6 — OpenID Provider (logout)

| Req | L | State | Notes |
|-----|---|-------|-------|
| V10.6.2 | 2 | ✅ | RP side hardened: `/logout` is an **antiforgery POST** → Logto `end_session_endpoint` with a registered `post_logout_redirect_uri` + open-redirect guard. |
