# V9 — Self-contained tokens

**ASVS 5.0 L2** · [← dashboard](README.md) · the JWT-bearer scheme for machine callers.

## Status summary

✅ Implemented — audience/issuer/lifetime validation in
[AuthenticationExtensions.cs](../../../src/KanbanApi/Auth/AuthenticationExtensions.cs). Tests that
pin valid-aud → 200 / wrong-aud → 401 / no-token → 401 land with the test suite (implementation phase).

---

| Req | L | State | Notes |
|-----|---|-------|-------|
| V9.1.1 | 1 | ✅ | `AddJwtBearer` validates the signature against the Logto JWKS before the principal is built. |
| V9.1.2 | 1 | ✅ | Algorithms constrained to those in the discovered JWKS (asymmetric, RS256/ES256); the handler rejects `alg:none`. |
| V9.1.3 | 1 | ✅ | `Authority = Logto:Issuer` pins the JWKS source; `jku`/`x5u`/`jwk` token headers ignored; `RequireHttpsMetadata=true` in non-dev. |
| V9.2.1 | 1 | ✅ | `exp`/`nbf` validated (`ValidateLifetime`, default on). |
| V9.2.2 | 2 | ✅ | Access tokens only for `/api/*` authorization; the BFF cookie path uses the ID-token-derived principal. Schemes don't cross-validate. |
| V9.2.3 | 2 | ✅ | `ValidateAudience = true`, `aud == Logto:Audience` (`https://kanban.api`). Cross-audience replay pinned by a test (implementation phase). |
| V9.2.4 | 2 | ✅ | Single API resource/audience in v1. A future MCP surface gets a **distinct** audience; reserve the name now. |
