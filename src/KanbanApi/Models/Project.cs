namespace KanbanApi.Models;

/// <summary>
/// Outcome of mirroring a project into the Calendar app (best-effort, on project create):
/// <see cref="Skipped"/> = mirroring not attempted (feature unconfigured), <see cref="Mirrored"/> =
/// a Calendar project was created (its id in <see cref="Project.CalendarProjectId"/>),
/// <see cref="Failed"/> = the attempt errored (logged; the Kanban project still saved).
/// See docs/ecosystem-integration.md §6.
/// </summary>
public enum ProjectMirrorStatus
{
    Skipped,
    Mirrored,
    Failed,
}

/// <summary>
/// A kanban project: a container of work with a date range, an owner (creator), and a set of
/// assignees. Tasks and board columns hang off a project (added in a later screen). Read access
/// is owner-or-assignee, enforced by the global query filter in
/// <see cref="Data.KanbanDbContext"/>; edit/delete/assignee changes are owner-only.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>
    /// Optional planned budget for the project. Currency-neutral — a bare amount, with no currency
    /// stored or assumed (the UI shows no currency symbol). Never negative.
    /// </summary>
    public decimal? Budget { get; set; }

    /// <summary>
    /// The Logto <c>sub</c> of the owner (creator). Owner-only: edit, delete, manage assignees.
    /// Set server-side from the authenticated user — never from a request payload.
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Audit: when/who created the project (the owner at creation time).</summary>
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Mirroring into the Calendar app (see docs/ecosystem-integration.md §6). Set server-side on
    /// create; never client-supplied. <see cref="MirrorStatus"/> records the best-effort outcome and
    /// <see cref="CalendarProjectId"/> holds the created Calendar project's id when
    /// <see cref="ProjectMirrorStatus.Mirrored"/> (null otherwise).
    /// </summary>
    public ProjectMirrorStatus MirrorStatus { get; set; } = ProjectMirrorStatus.Skipped;
    public string? CalendarProjectId { get; set; }

    /// <summary>
    /// Users (incl. the owner) who can see this project. Not serialized directly (would cycle and
    /// leak the raw list); exposed through the read model / a dedicated assignees endpoint.
    /// </summary>
    public ICollection<ProjectAssignee> Assignees { get; } = new List<ProjectAssignee>();
}
