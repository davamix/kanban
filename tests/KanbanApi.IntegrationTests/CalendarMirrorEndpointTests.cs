using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KanbanApi.Data;
using KanbanApi.IntegrationTests.Fakes;
using KanbanApi.IntegrationTests.Fixtures;
using KanbanApi.Models;
using KanbanApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// The Calendar mirror is a best-effort side effect of POST /api/projects: it records an outcome on
/// the project (surfaced on the response) but MUST NOT change whether creation succeeds. Driven by
/// <see cref="Fakes.FakeCalendarMirror"/>, whose outcome is chosen by the project name. See ADR 0009.
/// </summary>
[Collection("Api")]
public sealed class CalendarMirrorEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record ProjectDto(
        Guid Id, string Name, string OwnerId, bool IsOwner,
        string MirrorStatus, string? CalendarProjectId);

    private static object Body(string name, params string[] assigneeIds) =>
        new { name, description = "desc", startDate = "2026-07-01", endDate = "2026-08-01", assigneeIds };

    // The mirror is a singleton (see ApiFactory), so its recorded update/delete calls persist across the
    // collection — tests filter by their own project id to stay isolated.
    private FakeCalendarMirror Mirror => (FakeCalendarMirror)factory.Services.GetRequiredService<ICalendarMirror>();

    private async Task<ProjectDto> CreateAsync(string owner, string name, params string[] assigneeIds)
    {
        var res = await factory.CreateClientAs(owner).PostAsJsonAsync("/api/projects/", Body(name, assigneeIds));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
    }

    [Fact]
    public async Task Create_MirroredProject_SetsStatusAndCalendarId_OnResponseAndDb()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        var res = await factory.CreateClientAs(owner).PostAsJsonAsync("/api/projects/", Body("Synced project"));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        created.MirrorStatus.Should().Be("Mirrored");
        created.CalendarProjectId.Should().Be($"cal-{created.Id}");

        await factory.WithDbAsync(async db =>
        {
            // No HTTP context in this scope, so the owner-or-assignee filter would fail closed —
            // bypass it to assert the persisted row directly.
            var p = await db.Projects.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == created.Id);
            p.MirrorStatus.Should().Be(ProjectMirrorStatus.Mirrored);
            p.CalendarProjectId.Should().Be($"cal-{created.Id}");
        });
    }

    [Fact]
    public async Task Create_WhenMirrorFails_StillReturns201_AndRecordsFailed()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        // FakeCalendarMirror maps the MIRRORFAIL prefix to a Failed outcome.
        var res = await factory.CreateClientAs(owner).PostAsJsonAsync("/api/projects/", Body("MIRRORFAIL project"));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        created.MirrorStatus.Should().Be("Failed");
        created.CalendarProjectId.Should().BeNull();

        await factory.WithDbAsync(async db =>
        {
            var p = await db.Projects.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == created.Id);
            p.MirrorStatus.Should().Be(ProjectMirrorStatus.Failed);
            p.CalendarProjectId.Should().BeNull();
        });
    }

    [Fact]
    public async Task Create_WhenMirrorSkipped_StillReturns201_AndRecordsSkipped()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        // FakeCalendarMirror maps the MIRRORSKIP prefix to Skipped (the unconfigured/feature-off path).
        var res = await factory.CreateClientAs(owner).PostAsJsonAsync("/api/projects/", Body("MIRRORSKIP project"));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        created.MirrorStatus.Should().Be("Skipped");
        created.CalendarProjectId.Should().BeNull();
    }

    [Fact]
    public async Task List_CarriesMirrorStatus()
    {
        var owner = $"owner-{Guid.NewGuid()}";

        var res = await factory.CreateClientAs(owner).PostAsJsonAsync("/api/projects/", Body("Listed synced"));
        var created = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;

        var list = (await factory.CreateClientAs(owner).GetFromJsonAsync<List<ProjectDto>>("/api/projects/", Json))!;
        var seen = list.Should().ContainSingle(p => p.Id == created.Id).Subject;
        seen.MirrorStatus.Should().Be("Mirrored");
        seen.CalendarProjectId.Should().Be($"cal-{created.Id}");
    }

    [Fact]
    public async Task Edit_MirroredProject_PropagatesUpdate_AndReconcilesAssignees()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var created = await CreateAsync(owner, "Editable synced", "user-a");
        created.CalendarProjectId.Should().Be($"cal-{created.Id}");

        // Swap assignee user-a → user-b; owner stays and must never appear in the delta.
        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{created.Id}", Body("Editable synced", "user-b"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        updated.MirrorStatus.Should().Be("Mirrored");

        var call = Mirror.Updated.Should().ContainSingle(c => c.ProjectId == created.Id).Subject;
        call.Added.Should().BeEquivalentTo(["user-b"]);
        call.Removed.Should().BeEquivalentTo(["user-a"]);
        call.Added.Should().NotContain(owner);
        call.Removed.Should().NotContain(owner);
    }

    [Fact]
    public async Task Edit_NotMirroredProject_DoesNotPropagate_StillReturns200()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        // MIRRORSKIP → Skipped on create, so no CalendarProjectId ⇒ nothing to update.
        var created = await CreateAsync(owner, "MIRRORSKIP editable");
        created.CalendarProjectId.Should().BeNull();

        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{created.Id}", Body("MIRRORSKIP editable renamed"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        Mirror.Updated.Should().NotContain(c => c.ProjectId == created.Id);
    }

    [Fact]
    public async Task Edit_WhenUpdatePropagationFails_StillReturns200_AndRecordsFailed()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var created = await CreateAsync(owner, "Was synced");   // Mirrored, has a CalendarProjectId

        // Rename to the MIRRORFAIL prefix so the (now-invoked) update propagation reports Failed.
        var res = await factory.CreateClientAs(owner)
            .PutAsJsonAsync($"/api/projects/{created.Id}", Body("MIRRORFAIL now"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        updated.MirrorStatus.Should().Be("Failed");
        Mirror.Updated.Should().Contain(c => c.ProjectId == created.Id);

        await factory.WithDbAsync(async db =>
        {
            var p = await db.Projects.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == created.Id);
            p.MirrorStatus.Should().Be(ProjectMirrorStatus.Failed);
            p.CalendarProjectId.Should().Be($"cal-{created.Id}");   // counterpart still mapped (drift flag)
        });
    }

    [Fact]
    public async Task Delete_MirroredProject_PropagatesDelete_Returns204()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var created = await CreateAsync(owner, "Delete me synced");

        var res = await factory.CreateClientAs(owner).DeleteAsync($"/api/projects/{created.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Mirror.Deleted.Should().Contain(created.Id);
    }

    [Fact]
    public async Task Delete_NotMirroredProject_DoesNotPropagate_Returns204()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var created = await CreateAsync(owner, "MIRRORSKIP delete me");   // no CalendarProjectId
        created.CalendarProjectId.Should().BeNull();

        var res = await factory.CreateClientAs(owner).DeleteAsync($"/api/projects/{created.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Mirror.Deleted.Should().NotContain(created.Id);
    }

    [Fact]
    public async Task Edit_AsAssignee_IsForbidden_DoesNotPropagate()
    {
        // A non-owner assignee can see the mirrored project but must not edit it — and the block must
        // come before any Calendar propagation (an assignee must never drive a Calendar edit).
        var owner = $"owner-{Guid.NewGuid()}";
        var created = await CreateAsync(owner, "Owner-only editable", "user-b");
        created.CalendarProjectId.Should().Be($"cal-{created.Id}");   // genuinely mirrored

        var res = await factory.CreateClientAs("user-b")
            .PutAsJsonAsync($"/api/projects/{created.Id}", Body("Hijacked", "user-b"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Mirror.Updated.Should().NotContain(c => c.ProjectId == created.Id);
    }

    [Fact]
    public async Task Delete_AsAssignee_IsForbidden_DoesNotPropagate()
    {
        var owner = $"owner-{Guid.NewGuid()}";
        var created = await CreateAsync(owner, "Owner-only deletable", "user-b");
        created.CalendarProjectId.Should().Be($"cal-{created.Id}");

        var res = await factory.CreateClientAs("user-b").DeleteAsync($"/api/projects/{created.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Mirror.Deleted.Should().NotContain(created.Id);
    }
}
