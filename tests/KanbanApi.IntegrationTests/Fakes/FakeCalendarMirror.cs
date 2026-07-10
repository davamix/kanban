using KanbanApi.Models;
using KanbanApi.Services;

namespace KanbanApi.IntegrationTests.Fakes;

/// <summary>
/// Deterministic stand-in for <see cref="ICalendarMirror"/> — no network in tests. The outcome is
/// driven by the project name so a test can pick it: a name starting with <c>MIRRORFAIL</c> →
/// <see cref="ProjectMirrorStatus.Failed"/>; a name starting with <c>MIRRORSKIP</c> →
/// <see cref="ProjectMirrorStatus.Skipped"/>; anything else → <see cref="ProjectMirrorStatus.Mirrored"/>
/// with a stable id <c>cal-{project.Id}</c>. This mirrors how the real service reports outcomes without
/// standing up Logto + Calendar. Every call is recorded so edit/delete-propagation tests can assert the
/// mirror was (or was not) invoked and with which assignee delta.
/// </summary>
public sealed class FakeCalendarMirror : ICalendarMirror
{
    public sealed record UpdateCall(Guid ProjectId, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

    public List<UpdateCall> Updated { get; } = [];
    public List<Guid> Deleted { get; } = [];

    private static ProjectMirrorStatus Outcome(Project project) =>
        project.Name.StartsWith("MIRRORFAIL", StringComparison.Ordinal) ? ProjectMirrorStatus.Failed
        : project.Name.StartsWith("MIRRORSKIP", StringComparison.Ordinal) ? ProjectMirrorStatus.Skipped
        : ProjectMirrorStatus.Mirrored;

    public Task<(ProjectMirrorStatus Status, string? CalendarProjectId)> MirrorProjectAsync(
        Project project, IReadOnlyList<string> assigneeIds, CancellationToken ct = default) =>
        Outcome(project) switch
        {
            ProjectMirrorStatus.Failed => Task.FromResult((ProjectMirrorStatus.Failed, (string?)null)),
            ProjectMirrorStatus.Skipped => Task.FromResult((ProjectMirrorStatus.Skipped, (string?)null)),
            _ => Task.FromResult((ProjectMirrorStatus.Mirrored, (string?)$"cal-{project.Id}")),
        };

    public Task<ProjectMirrorStatus> UpdateProjectAsync(
        Project project, IReadOnlyList<string> addedAssigneeIds, IReadOnlyList<string> removedAssigneeIds,
        CancellationToken ct = default)
    {
        Updated.Add(new UpdateCall(project.Id, addedAssigneeIds, removedAssigneeIds));
        return Task.FromResult(Outcome(project));
    }

    public Task<bool> DeleteProjectAsync(Project project, CancellationToken ct = default)
    {
        Deleted.Add(project.Id);
        return Task.FromResult(true);
    }
}
