using KanbanApi.Models;
using KanbanApi.Services;

namespace KanbanApi.IntegrationTests.Fakes;

/// <summary>
/// Deterministic stand-in for <see cref="ICalendarMirror"/> — no network in tests. The outcome is
/// driven by the project name so a test can pick it: a name starting with <c>MIRRORFAIL</c> →
/// <see cref="ProjectMirrorStatus.Failed"/>; a name starting with <c>MIRRORSKIP</c> →
/// <see cref="ProjectMirrorStatus.Skipped"/>; anything else → <see cref="ProjectMirrorStatus.Mirrored"/>
/// with a stable id <c>cal-{project.Id}</c>. This mirrors how the real service reports outcomes without
/// standing up Logto + Calendar.
/// </summary>
public sealed class FakeCalendarMirror : ICalendarMirror
{
    public Task<(ProjectMirrorStatus Status, string? CalendarProjectId)> MirrorProjectAsync(
        Project project, IReadOnlyList<string> assigneeIds, CancellationToken ct = default)
    {
        if (project.Name.StartsWith("MIRRORFAIL", StringComparison.Ordinal))
            return Task.FromResult((ProjectMirrorStatus.Failed, (string?)null));
        if (project.Name.StartsWith("MIRRORSKIP", StringComparison.Ordinal))
            return Task.FromResult((ProjectMirrorStatus.Skipped, (string?)null));
        return Task.FromResult((ProjectMirrorStatus.Mirrored, (string?)$"cal-{project.Id}"));
    }
}
