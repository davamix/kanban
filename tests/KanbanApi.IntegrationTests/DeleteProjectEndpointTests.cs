using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KanbanApi.Data;
using KanbanApi.IntegrationTests.Fixtures;
using KanbanApi.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// DELETE /api/projects/{id} (drag-to-delete's write side): delete is owner-only. The mandatory
/// proofs are the authorization boundaries (ASVS V8) — an assignee cannot delete a project they
/// merely share (403), and a stranger gets a 404 that doesn't confirm the project exists.
/// </summary>
[Collection("Api")]
public sealed class DeleteProjectEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record ProjectDto(Guid Id, string Name);

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

    // Checks existence bypassing the per-user query filter, so "gone" means gone for everyone.
    private async Task<bool> ProjectExistsAsync(Guid id)
    {
        var exists = false;
        await factory.WithDbAsync(async db =>
            exists = await db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == id));
        return exists;
    }

    [Fact]
    public async Task Delete_Anonymous_Returns401()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Project", owner, owner);

        var res = await factory.CreateClient().DeleteAsync($"/api/projects/{id}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProjectExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_AsOwner_RemovesProject()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var member = $"member-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Doomed", owner, owner, member);

        var res = await factory.CreateClientAs(owner).DeleteAsync($"/api/projects/{id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ProjectExistsAsync(id)).Should().BeFalse();

        // And it no longer shows up in the owner's list.
        var list = (await factory.CreateClientAs(owner)
            .GetFromJsonAsync<List<ProjectDto>>("/api/projects/", Json))!;
        list.Should().NotContain(p => p.Id == id);
    }

    [Fact]
    public async Task Delete_AsAssignee_IsForbidden_AndProjectSurvives()
    {
        // An assignee can see the project but must not be able to delete it (owner-only, ASVS V8).
        var owner = $"owner-{Guid.NewGuid()}";
        var member = $"member-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Shared", owner, owner, member);

        var res = await factory.CreateClientAs(member).DeleteAsync($"/api/projects/{id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProjectExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_AsStranger_Returns404_WithoutLeakingExistence_AndProjectSurvives()
    {
        // A user who can't see the project gets a 404 (not a 403) so the response can't be used to
        // confirm the project exists. It must remain intact.
        var owner = $"owner-{Guid.NewGuid()}";
        var stranger = $"stranger-{Guid.NewGuid()}";
        var id = await SeedProjectAsync("Private", owner, owner);

        var res = await factory.CreateClientAs(stranger).DeleteAsync($"/api/projects/{id}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProjectExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        var res = await factory.CreateClientAs($"user-{Guid.NewGuid()}")
            .DeleteAsync($"/api/projects/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
