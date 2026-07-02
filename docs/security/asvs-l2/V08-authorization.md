# V8 — Authorization

**ASVS 5.0 L2** · [← dashboard](README.md) · the core chapter for Kanban's access model.

## Status summary

⏳ **Partial.** The authenticated API surface (`RequireAuthorization` + the cookie/JWT default
policy) is wired now. The **owner/assignee global query filter** and **owner checks** land with
the domain entities (projects/tasks) in the implementation phase, pinned by cross-user forgery
tests (see [../../testing.md](../../testing.md)).

---

## V8.1 — Authorization documentation

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.1.1 | 1 | ✅ | Function- and data-level rules documented in [../../auth.md](../../auth.md) §Access model + §Authorization. |
| V8.1.2 | 2 | ➖ | No field-level access differentiation planned — owner sees/edits all fields; assignees read all fields. |

## V8.2 — General authorization design

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.2.1 | 1 | ✅ | `RequireAuthorization()` on every `/api/*` group + the cookie/JWT default policy in [Program.cs](../../../src/KanbanApi/Program.cs). Anonymous → 401. |
| V8.2.2 | 1 | ⏳ | EF Core global query filter on `Project`/`Task` keyed off `ICurrentUser` — added with the domain entities in [KanbanDbContext.cs](../../../src/KanbanApi/Data/KanbanDbContext.cs). |
| V8.2.3 | 2 | ➖ | No field-level (BOPLA) differentiation — same rationale as V8.1.2. |

## V8.3 — Operation-level authorization

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.3.1 | 1 | ⏳ | Trusted-layer enforcement: endpoint `RequireAuthorization()` + owner check + DB query filter. Owner-only edit/delete/assign; visible-but-not-owner → 403, not-visible → 404. Implemented with the domain. |

## V8.4 — Other considerations

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.4.1 | 2 | ⏳ | Cross-user isolation: an unset `ICurrentUser` matches no row (fail closed); bypasses (`IgnoreQueryFilters()`) limited to seeding with a `// reason:`. Pinned by cross-user forgery tests. |
