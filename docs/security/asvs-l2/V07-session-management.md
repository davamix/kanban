# V7 — Session management

**ASVS 5.0 L2** · [← dashboard](README.md)

## Status summary

✅ Implemented — BFF cookie session ([AuthenticationExtensions.cs](../../../src/KanbanApi/Auth/AuthenticationExtensions.cs)).

---

| Req | L | State | Notes |
|-----|---|-------|-------|
| V7.1.1–7.1.3 | 1–2 | ✅ | RP cookie session bounded by both its own limits and Logto's session (silent renewal fails → re-auth). |
| V7.2.x | 1 | ✅ | Session established only after the OIDC code flow completes; principal built from validated tokens. |
| V7.3.1 / V7.3.2 | 2 | ✅ | Risk-based lifetimes: 12 h sliding inactivity, 7 d absolute cap (low-risk tool, low-friction re-auth via hosted page). |
| V7.4.x | 2 | ✅ | Logout = antiforgery POST → local sign-out + Logto `end_session`; revocation owned by Logto. |
| V7.5.x | 2 | ➖/⏳ | No in-app authentication-factor changes (managed by Logto), so no in-app sensitive-change re-auth path. |
| V7.6.1 | 2 | ✅ | Cookie flags: HttpOnly · Secure · SameSite=Lax · encrypted via the Data Protection key ring (persisted in Postgres). |
