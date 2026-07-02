# V8 — Authorization

**ASVS 5.0 L2** · [← dashboard](README.md) · the core chapter for Kanban's access model.

## Status summary

✅ **Implemented + tested** for the project read model; owner-gated mutations (V8.3) arrive with
the project-creation screen. The owner/assignee global query filter and the anonymous-401 gate are
pinned by [ProjectsEndpointTests](../../../tests/KanbanApi.IntegrationTests/ProjectsEndpointTests.cs).

---

## V8.1 — Authorization documentation

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.1.1 | 1 | ✅ | Function- and data-level rules documented in [../../auth.md](../../auth.md) §Access model + §Authorization. |
| V8.1.2 | 2 | ➖ | No field-level access differentiation planned — owner sees/edits all fields; assignees read all fields. |

## V8.2 — General authorization design

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.2.1 | 1 | ✅ | `RequireAuthorization()` on every `/api/*` group + the cookie/JWT default policy in [Program.cs](../../../src/KanbanApi/Program.cs). Anonymous → 401 (`List_Anonymous_Returns401`). |
| V8.2.2 | 1 | ✅ | EF Core global query filter on `Project` keyed off `ICurrentUser` ([KanbanDbContext.cs](../../../src/KanbanApi/Data/KanbanDbContext.cs)) — cross-user rows invisible; IDOR/BOLA blocked at the data layer (`List_ReturnsProjectsTheUserIsAssignedTo_MarkedAsShared`, `List_DoesNotReturnProjectsTheUserCannotSee_CrossUserForgery`). |
| V8.2.3 | 2 | ➖ | No field-level (BOPLA) differentiation — same rationale as V8.1.2. |

## V8.3 — Operation-level authorization

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.3.1 | 1 | ⏳ | Trusted-layer enforcement for mutations: endpoint `RequireAuthorization()` + owner check + DB query filter (owner-only edit/delete/assign; visible-but-not-owner → 403, not-visible → 404). Added with the project-creation screen — no mutating project endpoints yet. |

## V8.4 — Other considerations

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.4.1 | 2 | ✅ | Cross-user isolation: an unset `ICurrentUser` matches no row (fail closed). No `IgnoreQueryFilters()` bypasses. Pinned by the cross-user forgery test (`List_DoesNotReturnProjectsTheUserCannotSee_CrossUserForgery`). |
