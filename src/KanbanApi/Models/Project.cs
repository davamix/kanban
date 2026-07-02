namespace KanbanApi.Models;

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
    /// The Logto <c>sub</c> of the owner (creator). Owner-only: edit, delete, manage assignees.
    /// Set server-side from the authenticated user — never from a request payload.
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Audit: when/who created the project (the owner at creation time).</summary>
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Users (incl. the owner) who can see this project. Not serialized directly (would cycle and
    /// leak the raw list); exposed through the read model / a dedicated assignees endpoint.
    /// </summary>
    public ICollection<ProjectAssignee> Assignees { get; } = new List<ProjectAssignee>();
}
