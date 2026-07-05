# Testing guide

How to write and run Kanban's tests. The `integration-test-author` agent (added with the test
suite) treats this as the source of truth. Mirrors the ecosystem's Calendar app.

> **Status:** `tests/KanbanApi.IntegrationTests` exists (`ApiFactory` + Testcontainers Postgres +
> `TestAuthHandler`), with the project read model's visibility/forgery proofs in
> [ProjectsEndpointTests](../tests/KanbanApi.IntegrationTests/ProjectsEndpointTests.cs). A
> `KanbanApi.UnitTests` project is added when there's pure logic to cover (validators, transitions).

## Test projects

| Project | Kind | Use for |
|---|---|---|
| `tests/KanbanApi.UnitTests` _(planned)_ | Unit (no host) | Pure logic — validators, `CurrentUser` claim resolution, status/column transitions |
| `tests/KanbanApi.IntegrationTests` | HTTP integration | Endpoints, the access model, JWT validation, the directory |

Both projects are referenced by `Kanban.slnx` so `dotnet test Kanban.slnx` runs everything.

## Running

```bash
dotnet test Kanban.slnx                        # everything
dotnet test tests/KanbanApi.UnitTests          # fast, no Docker
dotnet test tests/KanbanApi.IntegrationTests   # boots a Testcontainers Postgres
```

Integration tests need a Docker daemon (the devcontainer has docker-in-docker).

## Fixtures

| Fixture | What it gives you |
|---|---|
| [`ApiFactory`](../tests/KanbanApi.IntegrationTests/Fixtures/ApiFactory.cs) | Real Postgres + `TestAuthHandler` (auth via the `X-Test-User` header), CSRF + Logto faked, env `Testing`. Default for endpoint/authz tests. |
| `JwtApiFactory` _(planned)_ | Real `JwtBearer` validated against a local test key; `MintJwt(aud, sub)`. For audience/issuer tests. |

- **`CreateClientAs("user-a")`** authenticates as that user (its value is the `sub`). No header → `401`.
- **`WithDbAsync(...)`** runs a DbContext scope for test setup (insert users/projects).
- The factory boots its own pgvector container in `InitializeAsync` and migrates on host start.

## Conventions

- Config goes through `builder.UseSetting("Key:Path", value)` — **never** `Environment.SetEnvironmentVariable`
  (process-wide; clobbers across parallel collections).
- Replace external clients (`ILogtoManagementClient`) with fakes via `ConfigureTestServices`.
- One container is shared per collection, so tests **must not** depend on each other: use unique
  ids (`Guid.NewGuid()`) and query by the returned id, not absolute counts.
- Assertions use **FluentAssertions** (`.Should()`), mocks use **NSubstitute**.
- Test names: `Method_Scenario_ExpectedResult`.

## Mandatory: cross-user forgery tests

Every feature touching owner/assignee data needs at least one isolation proof (ASVS V8):

1. Create a resource as user A.
2. Act as user B.
3. Assert `404` (not visible) or `403` (visible-but-not-owner) as appropriate.

Plus: anonymous → `401`, wrong-audience JWT → `401`, and (for tasks) an assign to a non-project
member → `400`/`403`.
