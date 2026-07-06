using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KanbanApi.IntegrationTests.Fixtures;
using KanbanApi.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// PUT /api/projects/{id} (the edit form's write side): edit is owner-only and replaces the editable
/// fields + reconciles the assignee set. The mandatory proofs are the authorization boundaries
/// (ASVS V8) — an assignee cannot edit a project they merely share (403), a stranger gets a 404 that
/// doesn't confirm existence — plus the invariant that the owner is never dropped from the assignees.
/// </summary>
[Collection("Api")]
public sealed class UpdateProjectEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record AssigneeDto(string Id, string? Name);
    private record ProjectDto(
        Guid Id, string Name, string? Description, string StartDate, string EndDate,
        decimal? Budget, string OwnerId, bool IsOwner, string Role, List<AssigneeDto> Assignees);

    // A valid update body; callers override fields as needed.
    private static object Body(
        string name = "Renamed", string? description = "updated",
        string startDate = "2026-07-01", string endDate = "2026-08-01",
        decimal? budget = null, string[]? assigneeIds = null) =>
        new { name, description, startDate, endDate, budget, assigneeIds = assigneeIds ?? [] };

    // Inserts a project owned by ownerId with the given assignees (users are created as needed).
    private async Task<Guid> SeedProjectAsync(string name, string ownerId, params string[] assignees)
    {
        var id = Guid.NewGuid();
        await factory.WithDbAsync(async db =>
        {
            foreach (var uid in new[] { ownerId }.Concat(assignees).Distinct())
                await db.EnsureUserAsync(uid);

            var project = new Project
            {
                Id = id,
                Name = name,
                Description = "seeded",
                StartDate = new DateOnly(2026, 7, 1),
                EndDate = new DateOnly(2026, 8, 1),
                Budget = 100m,
                OwnerId = ownerId,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = ownerId,
            };
            foreach (var uid in assignees.Distinct())
                project.Assignees.Add(new ProjectAssignee { ProjectId = id, UserId = uid });

            db.Projects.Add(project);
            await db.SaveChangesAsync();
        });
        return id;
    }

    // Reads a project's persisted state bypassing the per-user query filter (for assertions).
    private async Task<Project?> LoadAsync(Guid id)
    {
        Project? project = null;
        await factory.WithDbAsync(async db =>
            project = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Include(p => p.Assignees).FirstOrDefaultAsync(p => p.Id == id));
        return project;
    }

    [Fact]
    public async Task Update_Anonymous_Returns401()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Project", owner, owner);

        var res = await factory.CreateClient().PutAsJsonAsync($"/api/projects/{id}", Body());

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoadAsync(id))!.Name.Should().Be("Project");
    }

    [Fact]
    public async Task Update_AsOwner_ReplacesFields()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Before", owner, owner);

        var res = await factory.CreateClientAs(owner).PutAsJsonAsync($"/api/projects/{id}",
            Body(name: "After", description: "new desc", startDate: "2026-09-01", endDate: "2026-10-01", budget: 4200.50m));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        dto.Name.Should().Be("After");
        dto.Description.Should().Be("new desc");
        dto.StartDate.Should().Be("2026-09-01");
        dto.EndDate.Should().Be("2026-10-01");
        dto.Budget.Should().Be(4200.50m);
        dto.IsOwner.Should().BeTrue();
        dto.Role.Should().Be("owner");

        var persisted = (await LoadAsync(id))!;
        persisted.Name.Should().Be("After");
        persisted.Budget.Should().Be(4200.50m);
        persisted.OwnerId.Should().Be(owner);   // owner is immutable
    }

    [Fact]
    public async Task Update_AsOwner_ReconcilesAssignees_AndKeepsOwner()
    {
        // Start owner + user-a; update to user-b only. Expect user-a dropped, user-b added, and the
        // owner always retained even though the client didn't list them.
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Team", owner, owner, "user-a");

        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{id}", Body(assigneeIds: ["user-b"]));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        dto.Assignees.Select(a => a.Id).Should().BeEquivalentTo([owner, "user-b"]);
        dto.Assignees.Should().Contain(a => a.Id == "user-b" && a.Name == "User B");

        var persisted = (await LoadAsync(id))!;
        persisted.Assignees.Select(a => a.UserId).Should().BeEquivalentTo([owner, "user-b"]);
    }

    [Fact]
    public async Task Update_DropsAssigneesNotInDirectory()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Ghosts", owner, owner);

        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{id}", Body(assigneeIds: ["user-a", "ghost-not-a-user"]));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        dto.Assignees.Should().Contain(a => a.Id == "user-a");
        dto.Assignees.Should().NotContain(a => a.Id == "ghost-not-a-user");
    }

    [Fact]
    public async Task Update_AsAssignee_IsForbidden_AndUnchanged()
    {
        // An assignee can see the project but must not be able to edit it (owner-only, ASVS V8).
        var owner = $"owner-{Guid.NewGuid()}";
        var member = $"member-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Shared", owner, owner, member);

        var res = await factory.CreateClientAs(member)
            .PutAsJsonAsync($"/api/projects/{id}", Body(name: "Hijacked"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LoadAsync(id))!.Name.Should().Be("Shared");
    }

    [Fact]
    public async Task Update_AsStranger_Returns404_WithoutLeakingExistence_AndUnchanged()
    {
        // A user who can't see the project gets a 404 (not a 403) so the response can't confirm it
        // exists. It must remain intact.
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Private", owner, owner);

        var res = await factory.CreateClientAs(stranger)
            .PutAsJsonAsync($"/api/projects/{id}", Body(name: "Peeked"));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(id))!.Name.Should().Be("Private");
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        var res = await factory.CreateClientAs($"user-{Guid.NewGuid()}")
            .PutAsJsonAsync($"/api/projects/{Guid.NewGuid()}", Body());

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_MissingName_ReturnsValidationProblem()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Named", owner, owner);

        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{id}", Body(name: "   "));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();
        (await LoadAsync(id))!.Name.Should().Be("Named");   // rejected write leaves it untouched
    }

    [Fact]
    public async Task Update_EndDateBeforeStart_ReturnsValidationProblem()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Dated", owner, owner);

        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{id}", Body(startDate: "2026-08-01", endDate: "2026-07-01"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("endDate", out _).Should().BeTrue();
    }
}
