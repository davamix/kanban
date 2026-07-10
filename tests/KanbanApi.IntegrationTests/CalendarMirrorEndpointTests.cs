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

    private static object Body(string name) =>
        new { name, description = "desc", startDate = "2026-07-01", endDate = "2026-08-01", assigneeIds = Array.Empty<string>() };

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
}
