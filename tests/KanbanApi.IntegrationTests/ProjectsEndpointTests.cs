using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KanbanApi.Data;
using KanbanApi.IntegrationTests.Fixtures;
using KanbanApi.Models;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// The project-selection read model: GET /api/projects returns only the projects the caller owns
/// or is assigned to, with the caller's relationship resolved. Cross-user isolation (ASVS V8) is
/// the mandatory proof — see docs/testing.md.
/// </summary>
[Collection("Api")]
public sealed class ProjectsEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record AssigneeDto(string Id, string? Name);
    private record ProjectDto(
        Guid Id, string Name, string? Description, string StartDate, string EndDate,
        string OwnerId, bool IsOwner, string Role, List<AssigneeDto> Assignees);

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

    private static async Task<List<ProjectDto>> ReadProjectsAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<List<ProjectDto>>(Json))!;
    }

    [Fact]
    public async Task List_Anonymous_Returns401()
    {
        var res = await factory.CreateClient().GetAsync("/api/projects/");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_ReturnsProjectsTheUserOwns_MarkedAsOwner()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Owned project", owner, owner);

        var projects = await ReadProjectsAsync(await factory.CreateClientAs(owner).GetAsync("/api/projects/"));

        var mine = projects.Should().ContainSingle(p => p.Id == id).Subject;
        mine.IsOwner.Should().BeTrue();
        mine.Role.Should().Be("owner");
    }

    [Fact]
    public async Task List_ReturnsProjectsTheUserIsAssignedTo_MarkedAsShared()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var member = $"member-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Shared project", owner, owner, member);

        var projects = await ReadProjectsAsync(await factory.CreateClientAs(member).GetAsync("/api/projects/"));

        var shared = projects.Should().ContainSingle(p => p.Id == id).Subject;
        shared.IsOwner.Should().BeFalse();
        shared.Role.Should().Be("assignee");
        shared.Assignees.Should().Contain(a => a.Id == member);
    }

    [Fact]
    public async Task List_DoesNotReturnProjectsTheUserCannotSee_CrossUserForgery()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Private project", owner, owner); // stranger not assigned

        var projects = await ReadProjectsAsync(await factory.CreateClientAs(stranger).GetAsync("/api/projects/"));

        projects.Should().NotContain(p => p.Id == id);
    }
}
