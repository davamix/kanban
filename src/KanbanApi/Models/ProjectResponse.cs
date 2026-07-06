namespace KanbanApi.Models;

/// <summary>A single assignee, resolved to a display name for the UI (avatar + tooltip).</summary>
public sealed record AssigneeSummary(string Id, string? Name);

/// <summary>
/// Read model for the project-selection screen. Carries exactly what a project card needs, with
/// the current user's relationship (<see cref="IsOwner"/> / <see cref="Role"/>) resolved
/// server-side so the client never infers access from raw ownership fields.
/// </summary>
public sealed record ProjectResponse(
    Guid Id,
    string Name,
    string? Description,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal? Budget,
    string OwnerId,
    bool IsOwner,
    string Role,
    IReadOnlyList<AssigneeSummary> Assignees);

/// <summary>
/// Create-project payload from the creation form. The owner is taken from the authenticated
/// session server-side (never the request); <see cref="AssigneeIds"/> are Logto <c>sub</c>s picked
/// from the assignee directory (the owner is always added as an assignee). "Client / Organization"
/// on the form is a placeholder with no domain field yet, so it is intentionally absent here.
/// </summary>
public sealed record CreateProjectRequest(
    string Name,
    string? Description,
    DateOnly? StartDate,
    DateOnly? EndDate,
    decimal? Budget,
    IReadOnlyList<string>? AssigneeIds);

/// <summary>
/// Edit-project payload from the edit form. Same shape as <see cref="CreateProjectRequest"/> — the
/// whole editable project is replaced. Editing is owner-only (the owner is never changed and never
/// comes from the payload); the owner is always kept among <see cref="AssigneeIds"/> server-side.
/// </summary>
public sealed record UpdateProjectRequest(
    string Name,
    string? Description,
    DateOnly? StartDate,
    DateOnly? EndDate,
    decimal? Budget,
    IReadOnlyList<string>? AssigneeIds);
