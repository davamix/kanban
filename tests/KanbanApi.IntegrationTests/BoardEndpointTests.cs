using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KanbanApi.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// The board surface (/api/projects/{id}/board|columns|tasks). Proves: per-user visibility (a
/// stranger gets 404, an assignee is a member), the owner/member authorization split (columns are
/// owner-only, tasks are open to any member), the task-assignee restriction, task move/reorder, and
/// that a non-empty column can't be deleted. Subjects come from the session, never the payload.
/// </summary>
[Collection("Api")]
public sealed class BoardEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record AssigneeDto(string Id, string? Name);
    private record TaskDto(
        Guid Id, Guid ColumnId, string Title, string? Description, string Priority,
        string? AssigneeId, string? AssigneeName, string? DueDate, List<string> Labels, int Position);
    private record ColumnDto(Guid Id, string Name, int Position, List<TaskDto> Tasks);
    private record BoardDto(Guid ProjectId, string ProjectName, bool IsOwner, List<AssigneeDto> Assignees, List<ColumnDto> Columns);

    // Create a project owned by `owner` with `user-b` as an assignee; returns its id.
    private async Task<Guid> CreateProjectAsync(string owner)
    {
        var res = await factory.CreateClientAs(owner).PostAsJsonAsync("/api/projects/", new
        {
            name = "Board Project",
            description = "desc",
            startDate = "2026-07-01",
            endDate = "2026-08-01",
            assigneeIds = new[] { "user-b" },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await res.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    private async Task<BoardDto> GetBoardAsync(string user, Guid projectId)
    {
        var board = await factory.CreateClientAs(user)
            .GetFromJsonAsync<BoardDto>($"/api/projects/{projectId}/board", Json);
        return board!;
    }

    private static object TaskBody(
        string title = "A task", Guid? columnId = null, string? assigneeId = null,
        string priority = "Medium", string? dueDate = null, string[]? labels = null) =>
        new { title, description = "d", priority, assigneeId, dueDate, labels = labels ?? [], columnId };

    [Fact]
    public async Task Board_Anonymous_Returns401()
    {
        var res = await factory.CreateClient().GetAsync($"/api/projects/{Guid.NewGuid()}/board");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnauthenticatedBoardNavigation_RedirectsToLogin_PreservingReturnUrl()
    {
        // An unauthenticated HTML navigation to a board deep link redirects to /login carrying the
        // requested path, so silent re-auth returns the user to the board (not the default page).
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/board.html?project=abc123");
        req.Headers.Accept.ParseAdd("text/html");

        var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!.ToString();
        location.Should().StartWith("/login?returnUrl=");
        Uri.UnescapeDataString(location).Should().Contain("/board.html?project=abc123");
    }

    [Fact]
    public async Task Board_NewProject_HasDefaultColumns_OwnerFlagSet()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var board = await GetBoardAsync(owner, projectId);
        board.IsOwner.Should().BeTrue();
        board.Columns.Select(c => c.Name).Should().Equal("TODO", "WIP", "DONE");
        board.Columns.Select(c => c.Position).Should().Equal(0, 1, 2);
        board.Assignees.Should().Contain(a => a.Id == owner).And.Contain(a => a.Id == "user-b");
    }

    [Fact]
    public async Task Board_HiddenFromStranger_Returns404()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        // user-c is not a member; the project must read as absent, not forbidden (no existence leak).
        var res = await factory.CreateClientAs(stranger).GetAsync($"/api/projects/{projectId}/board");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Board_VisibleToAssignee_AsMember_NotOwner()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var board = await GetBoardAsync("user-b", projectId);
        board.IsOwner.Should().BeFalse();
    }

    [Fact]
    public async Task CreateTask_DefaultsToFirstColumn_AndMedium()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var res = await factory.CreateClientAs(owner)
            .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody(title: "First"));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await res.Content.ReadFromJsonAsync<TaskDto>(Json))!;

        var board = await GetBoardAsync(owner, projectId);
        var todo = board.Columns.Single(c => c.Position == 0);
        task.ColumnId.Should().Be(todo.Id);
        task.Priority.Should().Be("Medium");
        todo.Tasks.Should().ContainSingle(t => t.Id == task.Id);
    }

    [Fact]
    public async Task CreateTask_ByAssignee_IsAllowed()
    {
        // Tasks are collaborative: a non-owner member may create them.
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var res = await factory.CreateClientAs("user-b")
            .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody(title: "By assignee"));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTask_ByStranger_Returns404()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var res = await factory.CreateClientAs(stranger)
            .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody());
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTask_AssigneeMustBeProjectAssignee()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        // user-b is an assignee → allowed; user-c is not → 400 with an assigneeId field error.
        var ok = await factory.CreateClientAs(owner)
            .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody(assigneeId: "user-b"));
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ok.Content.ReadFromJsonAsync<TaskDto>(Json))!.AssigneeId.Should().Be("user-b");

        var bad = await factory.CreateClientAs(owner)
            .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody(assigneeId: "user-c"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("assigneeId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateTask_MissingTitle_ReturnsValidationProblem()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var res = await factory.CreateClientAs(owner)
            .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody(title: "   "));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("title", out _).Should().BeTrue();
    }

    [Fact]
    public async Task MoveTask_ChangesColumnAndReindexes()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var board = await GetBoardAsync(owner, projectId);
        var todo = board.Columns.Single(c => c.Position == 0);
        var wip = board.Columns.Single(c => c.Position == 1);
        var client = factory.CreateClientAs(owner);

        // Two tasks in TODO.
        var t1 = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("T1", todo.Id)))
            .Content.ReadFromJsonAsync<TaskDto>(Json))!;
        await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("T2", todo.Id));

        // Move T1 to WIP, position 0.
        var move = await client.PutAsJsonAsync($"/api/projects/{projectId}/tasks/{t1.Id}/move",
            new { columnId = wip.Id, position = 0 });
        move.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetBoardAsync(owner, projectId);
        after.Columns.Single(c => c.Id == wip.Id).Tasks.Should().ContainSingle(t => t.Id == t1.Id);
        after.Columns.Single(c => c.Id == todo.Id).Tasks.Should().NotContain(t => t.Id == t1.Id);
        // TODO's remaining task is reindexed to position 0.
        after.Columns.Single(c => c.Id == todo.Id).Tasks.Single().Position.Should().Be(0);
    }

    [Fact]
    public async Task Columns_CreateRenameReorderDelete_OwnerOnly()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var ownerClient = factory.CreateClientAs(owner);
        var assigneeClient = factory.CreateClientAs("user-b");

        // Assignee (a member, not owner) is forbidden from every column mutation.
        (await assigneeClient.PostAsJsonAsync($"/api/projects/{projectId}/columns", new { name = "TESTING" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Owner appends a column, then it lands last in the workflow.
        var create = await ownerClient.PostAsJsonAsync($"/api/projects/{projectId}/columns", new { name = "TESTING" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var newColumn = (await create.Content.ReadFromJsonAsync<ColumnDto>(Json))!;
        newColumn.Position.Should().Be(3);

        // Reorder: move the new column to the front.
        var board = await GetBoardAsync(owner, projectId);
        var order = new[] { newColumn.Id }.Concat(board.Columns.Where(c => c.Id != newColumn.Id).Select(c => c.Id)).ToArray();
        (await ownerClient.PutAsJsonAsync($"/api/projects/{projectId}/columns/order", new { orderedIds = order }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetBoardAsync(owner, projectId)).Columns.First().Id.Should().Be(newColumn.Id);

        // Rename it.
        (await ownerClient.PutAsJsonAsync($"/api/projects/{projectId}/columns/{newColumn.Id}", new { name = "QA" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetBoardAsync(owner, projectId)).Columns.Should().Contain(c => c.Name == "QA");

        // Delete it (empty → allowed).
        (await ownerClient.DeleteAsync($"/api/projects/{projectId}/columns/{newColumn.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetBoardAsync(owner, projectId)).Columns.Should().NotContain(c => c.Id == newColumn.Id);
    }

    [Fact]
    public async Task DeleteColumn_NonEmpty_Returns409()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var client = factory.CreateClientAs(owner);
        var board = await GetBoardAsync(owner, projectId);
        var todo = board.Columns.Single(c => c.Position == 0);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("Blocker", todo.Id));

        var res = await client.DeleteAsync($"/api/projects/{projectId}/columns/{todo.Id}");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReorderColumns_WithWrongIds_Returns400()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);

        var res = await factory.CreateClientAs(owner).PutAsJsonAsync(
            $"/api/projects/{projectId}/columns/order", new { orderedIds = new[] { Guid.NewGuid() } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTask_ByMember_RemovesIt()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var ownerClient = factory.CreateClientAs(owner);
        var task = (await (await ownerClient.PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("Doomed")))
            .Content.ReadFromJsonAsync<TaskDto>(Json))!;

        // A different member (the assignee) can delete it.
        (await factory.CreateClientAs("user-b").DeleteAsync($"/api/projects/{projectId}/tasks/{task.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var board = await GetBoardAsync(owner, projectId);
        board.Columns.SelectMany(c => c.Tasks).Should().NotContain(t => t.Id == task.Id);
    }

    [Fact]
    public async Task UpdateTask_MovesColumn_AndPersistsFields()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var client = factory.CreateClientAs(owner);
        var board = await GetBoardAsync(owner, projectId);
        var todo = board.Columns.Single(c => c.Position == 0);
        var done = board.Columns.Single(c => c.Position == 2);

        var task = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("Edit me", todo.Id)))
            .Content.ReadFromJsonAsync<TaskDto>(Json))!;

        var res = await client.PutAsJsonAsync($"/api/projects/{projectId}/tasks/{task.Id}", new
        {
            title = "Edited",
            description = "new",
            priority = "High",
            assigneeId = "user-b",
            dueDate = "2026-07-20",
            labels = new[] { "backend", "urgent" },
            columnId = done.Id,
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await res.Content.ReadFromJsonAsync<TaskDto>(Json))!;
        updated.Title.Should().Be("Edited");
        updated.Priority.Should().Be("High");
        updated.AssigneeId.Should().Be("user-b");
        updated.ColumnId.Should().Be(done.Id);
        updated.Labels.Should().BeEquivalentTo("backend", "urgent");
        updated.DueDate.Should().Be("2026-07-20");
    }

    [Fact]
    public async Task Columns_RenameReorderDelete_ByAssignee_AreForbidden()
    {
        // The owner-only boundary holds for every column verb, not just create.
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var board = await GetBoardAsync(owner, projectId);
        var col = board.Columns.First();
        var order = board.Columns.Select(c => c.Id).Reverse().ToArray();
        var assignee = factory.CreateClientAs("user-b");

        (await assignee.PutAsJsonAsync($"/api/projects/{projectId}/columns/{col.Id}", new { name = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await assignee.PutAsJsonAsync($"/api/projects/{projectId}/columns/order", new { orderedIds = order }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await assignee.DeleteAsync($"/api/projects/{projectId}/columns/{col.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateAndMoveTask_ByStranger_Return404_NoLeak()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var board = await GetBoardAsync(owner, projectId);
        var task = (await (await factory.CreateClientAs(owner)
                .PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("Secret")))
            .Content.ReadFromJsonAsync<TaskDto>(Json))!;
        var strangerClient = factory.CreateClientAs(stranger);

        (await strangerClient.PutAsJsonAsync($"/api/projects/{projectId}/tasks/{task.Id}",
            new { title = "hax", priority = "Low", columnId = board.Columns[0].Id, labels = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.PutAsJsonAsync($"/api/projects/{projectId}/tasks/{task.Id}/move",
            new { columnId = board.Columns[1].Id, position = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProject_WithTasks_Succeeds()
    {
        // Regression for the diamond cascade: project → {columns, tasks} both cascade, and the
        // task→column FK must not fire an immediate check that aborts the delete.
        var owner = $"owner-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var client = factory.CreateClientAs(owner);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", TaskBody("Has a task"));

        (await client.DeleteAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/projects/{projectId}/board")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TaskAndColumn_HiddenProject_Return404_NoLeak()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var projectId = await CreateProjectAsync(owner);
        var strangerClient = factory.CreateClientAs(stranger);

        // A non-member touching columns/tasks on a project they can't see gets 404 throughout.
        (await strangerClient.PostAsJsonAsync($"/api/projects/{projectId}/columns", new { name = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.DeleteAsync($"/api/projects/{projectId}/tasks/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
