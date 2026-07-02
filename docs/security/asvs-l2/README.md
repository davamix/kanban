# Kanban — OWASP ASVS 5.0 L2 verification

Per-chapter tracking of the OWASP ASVS 5.0 **Level 2** baseline for Kanban, mirroring the
structure proven in the ecosystem's Calendar app. **L2 is cumulative** (every L1 requirement
also applies at L2); L3-only requirements are out of scope.

> Source: [`OWASP/ASVS@v5.0.0`](https://github.com/OWASP/ASVS/tree/v5.0.0).
>
> **Infra-phase baseline.** The auth stack (BFF cookie + JWT bearer, sessions, config,
> least-privilege DB role) is wired now and reflected below. Rows that depend on the **domain**
> (V8 owner/assignee query filter + owner checks, and the tests that pin V8/V9) are ⏳ **Planned**
> until the implementation phase lands them.

## Status legend

✅ Pass · ❌ Fail · ❓ Unknown · ➖ N/A · ⏳ Planned (target a later milestone, not yet implemented)

**Evidence format:** `file:line`, config key, PR #, or a short note.

## Dashboard

| Chapter | Focus for Kanban | State |
|---|---|---|
| V1 Encoding & Sanitization | output encoding in the SPA (`escapeHtml`) | ⏳ (UI later) |
| V2 Validation & Business Logic | request-DTO validation; rate limiting | ⏳ (rate limiting ✅) |
| V3 Web Frontend Security | CSRF/antiforgery, security headers, no open redirect | ✅ antiforgery + redirect guard wired |
| V4 API & Web Service | JSON content types, no transparent HTTP→HTTPS on the API | ⏳ triage |
| V6 Authentication | delegated to Logto; passwords N/A if passwordless | ⏳ triage |
| [V7 Session Management](V07-session-management.md) | BFF cookie + Logto session lifetimes | ✅ implemented |
| [V8 Authorization](V08-authorization.md) | RequireAuthorization + global query filter + owner check | ⏳ partial (auth surface ✅; domain filter/owner checks planned) |
| [V9 Self-contained Tokens](V09-self-contained-tokens.md) | JWT validation hardening | ✅ implemented (tests planned) |
| [V10 OAuth & OIDC](V10-oauth-and-oidc.md) | BFF token handling, dual-scheme, logout | ✅ implemented |
| V11 Cryptography | delegated to Data Protection key ring / Logto / TLS | ⏳ triage |
| V12 Secure Communication | TLS at the reverse proxy | ⏳ triage |
| [V13 Configuration](V13-configuration.md) | least-privilege DB role, secrets via env | ✅ implemented |
| V14 Data Protection | `Cache-Control: no-store` on API responses | ✅ wired in Program.cs |
| V15 Secure Coding & Architecture | dependency scanning (CI), config-driven design | ✅ CI gates (Trivy + NuGet audit) |
| V16 Security Logging & Error Handling | RFC 9457 errors, security-event logging | ⏳ triage |

## Out of scope

- **V5 File Handling** — no user file uploads.
- **V17 WebRTC** — no peer-to-peer features.

## Process

1. Implement the control, then flip the row from ⏳ to ✅ with `file:line` / PR evidence.
2. Keep the chapter's status summary + this dashboard in sync.
3. The `security-reviewer` and `access-control-reviewer` agents (`.claude/agents/`) help close
   V7/V8/V9/V10/V13 rows on each diff.
