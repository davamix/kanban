# V8 — Authorization

**ASVS 5.0 L2** · [← dashboard](README.md) · the core chapter for Kanban's access model.

## Status summary

✅ **Implemented + tested** for the project read model **and the owner-gated mutations**
(`DELETE /api/projects/{id}`, ADR 0005; `PUT /api/projects/{id}`, ADR 0006). The owner/assignee global
query filter and the anonymous-401 gate are pinned by [ProjectsEndpointTests](../../../tests/KanbanApi.IntegrationTests/ProjectsEndpointTests.cs);
the owner-only delete boundary (owner → 204, assignee → 403, stranger → 404 with no existence leak) by
[DeleteProjectEndpointTests](../../../tests/KanbanApi.IntegrationTests/DeleteProjectEndpointTests.cs), and
the identical owner-only edit boundary (owner → 200, assignee → 403, stranger → 404) by
[UpdateProjectEndpointTests](../../../tests/KanbanApi.IntegrationTests/UpdateProjectEndpointTests.cs).

The **board** (ADR 0007) extends the same model to columns + tasks: `BoardColumn`/`TaskItem` inherit
the project's owner-or-assignee query filter (fail-closed), columns are **owner-only** and tasks are
open to **any member**, and a task's assignee is restricted to the project's assignees — all pinned by
[BoardEndpointTests](../../../tests/KanbanApi.IntegrationTests/BoardEndpointTests.cs).

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
| V8.2.2 | 1 | ✅ | EF Core global query filter on `Project` keyed off `ICurrentUser` ([KanbanDbContext.cs](../../../src/KanbanApi/Data/KanbanDbContext.cs)) — cross-user rows invisible; IDOR/BOLA blocked at the data layer (`List_ReturnsProjectsTheUserIsAssignedTo_MarkedAsShared`, `List_DoesNotReturnProjectsTheUserCannotSee_CrossUserForgery`). Matching filters on `BoardColumn`/`TaskItem` inherit the project's owner-or-assignee visibility (ADR 0007); a stranger hitting a project's board/columns/tasks gets `404` (`Board_HiddenFromStranger_Returns404`, `TaskAndColumn_HiddenProject_Return404_NoLeak`). |
| V8.2.3 | 2 | ➖ | No field-level (BOPLA) differentiation — same rationale as V8.1.2. |

## V8.3 — Operation-level authorization

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.3.1 | 1 | ✅ | Trusted-layer enforcement for mutations: endpoint `RequireAuthorization()` + owner check + DB query filter. `DELETE /api/projects/{id}` ([EfProjectStore.DeleteAsync](../../../src/KanbanApi/Services/EfProjectStore.cs), [ProjectEndpoints.cs](../../../src/KanbanApi/Endpoints/ProjectEndpoints.cs)) loads through the query filter then re-checks ownership: owner-only delete, visible-but-not-owner → 403, not-visible → 404 (no existence leak). Pinned by `DeleteProjectEndpointTests` (`Delete_AsAssignee_IsForbidden_AndProjectSurvives`, `Delete_AsStranger_Returns404_WithoutLeakingExistence_AndProjectSurvives`). `PUT /api/projects/{id}` ([EfProjectStore.UpdateAsync](../../../src/KanbanApi/Services/EfProjectStore.cs)) applies the same load-through-filter-then-owner-check pattern for editing — pinned by `UpdateProjectEndpointTests` (`Update_AsAssignee_IsForbidden_AndUnchanged`, `Update_AsStranger_Returns404_WithoutLeakingExistence_AndUnchanged`). Assignee management follows the same pattern when added. |
| V8.3.1 | 1 | ✅ | **Board (ADR 0007):** two tiers on the same project in [EfBoardStore.cs](../../../src/KanbanApi/Services/EfBoardStore.cs). **Columns are owner-only** — the load-through-filter-then-owner-check pattern gates create/rename/reorder/delete (owner → ok, assignee → `403`, stranger → `404`), pinned by `Columns_CreateRenameReorderDelete_OwnerOnly`. **Tasks are any-member** — visibility equals membership, so a member creates/edits/moves/deletes and a stranger gets `404` (`CreateTask_ByAssignee_IsAllowed`, `CreateTask_ByStranger_Returns404`, `DeleteTask_ByMember_RemovesIt`). A task's assignee is restricted to the project's assignees (`CreateTask_AssigneeMustBeProjectAssignee`); a non-empty column can't be deleted (`DeleteColumn_NonEmpty_Returns409`). |

## V8.4 — Other considerations

| Req | L | State | Notes |
|-----|---|-------|-------|
| V8.4.1 | 2 | ✅ | Cross-user isolation: an unset `ICurrentUser` matches no row (fail closed). No `IgnoreQueryFilters()` bypasses. Pinned by the cross-user forgery test (`List_DoesNotReturnProjectsTheUserCannotSee_CrossUserForgery`). |
