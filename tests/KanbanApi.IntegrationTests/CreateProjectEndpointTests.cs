using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KanbanApi.IntegrationTests.Fixtures;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// POST /api/projects (the creation form's write side): the caller becomes the owner and is always
/// an assignee, requested assignees are validated against the Logto directory (unknown ids dropped),
/// budget is persisted, and shape validation returns RFC 9457 problem details. The owner comes from
/// the session, never the payload (ASVS V8).
/// </summary>
[Collection("Api")]
public sealed class CreateProjectEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record AssigneeDto(string Id, string? Name);
    private record ProjectDto(
        Guid Id, string Name, string? Description, string StartDate, string EndDate,
        decimal? Budget, string OwnerId, bool IsOwner, string Role, List<AssigneeDto> Assignees);

    // A valid create body; callers override fields as needed.
    private static object Body(
        string name = "New Project", string? description = "desc",
        string startDate = "2026-07-01", string endDate = "2026-08-01",
        decimal? budget = null, string[]? assigneeIds = null) =>
        new { name, description, startDate, endDate, budget, assigneeIds = assigneeIds ?? [] };

    [Fact]
    public async Task Create_Anonymous_Returns401()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/projects/", Body());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_ReturnsCreated_OwnerAutoAssigned_BudgetPersisted()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        var res = await factory.CreateClientAs(owner)
            .PostAsJsonAsync("/api/projects/", Body(name: "Budgeted", budget: 12500.50m));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        created.IsOwner.Should().BeTrue();
        created.Role.Should().Be("owner");
        created.Budget.Should().Be(12500.50m);
        created.Assignees.Should().Contain(a => a.Id == owner);

        // And it now shows up in the owner's list.
        var list = (await factory.CreateClientAs(owner).GetFromJsonAsync<List<ProjectDto>>("/api/projects/", Json))!;
        list.Should().Contain(p => p.Id == created.Id);
    }

    [Fact]
    public async Task Create_IncludesDirectoryAssignee_WithResolvedName()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        var res = await factory.CreateClientAs(owner)
            .PostAsJsonAsync("/api/projects/", Body(assigneeIds: ["user-b"]));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        created.Assignees.Should().Contain(a => a.Id == owner);
        created.Assignees.Should().Contain(a => a.Id == "user-b" && a.Name == "User B");
    }

    [Fact]
    public async Task Create_DropsAssigneesNotInDirectory()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        var res = await factory.CreateClientAs(owner)
            .PostAsJsonAsync("/api/projects/", Body(assigneeIds: ["user-a", "ghost-not-a-user"]));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        created.Assignees.Should().Contain(a => a.Id == "user-a");
        created.Assignees.Should().NotContain(a => a.Id == "ghost-not-a-user");
    }

    [Fact]
    public async Task Create_MissingName_ReturnsValidationProblem()
    {
        var res = await factory.CreateClientAs($"owner-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/projects/", Body(name: "   "));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Create_EndDateBeforeStart_ReturnsValidationProblem()
    {
        var res = await factory.CreateClientAs($"owner-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/projects/", Body(startDate: "2026-08-01", endDate: "2026-07-01"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("endDate", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Create_MissingDates_ReturnsValidationProblem()
    {
        // A machine caller that omits the dates: the server (not just the client) must reject them,
        // rather than silently defaulting to 0001-01-01.
        var res = await factory.CreateClientAs($"owner-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/projects/", new { name = "No dates" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("startDate", out _).Should().BeTrue();
        errors.TryGetProperty("endDate", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Create_ProjectIsVisibleToAssignee_ButNotToStranger()
    {
        // Cross-user isolation for the write path: a project created with an assignee is visible to
        // that assignee and to nobody else. Mirrors the read-path forgery proof.
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";

        var res = await factory.CreateClientAs(owner)
            .PostAsJsonAsync("/api/projects/", Body(name: "Shared via create", assigneeIds: ["user-b"]));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;

        // The assignee (user-b) sees it, marked as a non-owner.
        var asAssignee = (await factory.CreateClientAs("user-b")
            .GetFromJsonAsync<List<ProjectDto>>("/api/projects/", Json))!;
        var seen = asAssignee.Should().ContainSingle(p => p.Id == created.Id).Subject;
        seen.IsOwner.Should().BeFalse();
        seen.Role.Should().Be("assignee");

        // A stranger does not.
        var asStranger = (await factory.CreateClientAs(stranger)
            .GetFromJsonAsync<List<ProjectDto>>("/api/projects/", Json))!;
        asStranger.Should().NotContain(p => p.Id == created.Id);
    }
}
