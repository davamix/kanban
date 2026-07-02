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
    string OwnerId,
    bool IsOwner,
    string Role,
    IReadOnlyList<AssigneeSummary> Assignees);
