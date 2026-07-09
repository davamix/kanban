namespace KanbanApi.Models;

// ---------------------------------------------------------------------------
// Read models — what the board screen renders. Shaped server-side so the client
// never infers access or status from raw entity fields.
// ---------------------------------------------------------------------------

/// <summary>A task as it appears on a board card. The assignee is resolved to a display name.</summary>
public sealed record TaskResponse(
    Guid Id,
    Guid ColumnId,
    string Title,
    string? Description,
    TaskPriority Priority,
    string? AssigneeId,
    string? AssigneeName,
    DateOnly? DueDate,
    IReadOnlyList<string> Labels,
    int Position);

/// <summary>A column with its ordered tasks. Order in the board = the workflow order.</summary>
public sealed record BoardColumnResponse(
    Guid Id,
    string Name,
    int Position,
    IReadOnlyList<TaskResponse> Tasks);

/// <summary>
/// Everything the board screen needs in one call: the project heading, whether the caller owns it
/// (so the client can gate the owner-only column controls), the project's assignees (the *only*
/// users a task may be assigned to — the task form's picker is restricted to these), and the
/// ordered columns each carrying their tasks.
/// </summary>
public sealed record BoardResponse(
    Guid ProjectId,
    string ProjectName,
    bool IsOwner,
    IReadOnlyList<AssigneeSummary> Assignees,
    IReadOnlyList<BoardColumnResponse> Columns);

// ---------------------------------------------------------------------------
// Write payloads.
// ---------------------------------------------------------------------------

/// <summary>Create/rename a column (owner-only). New columns are appended at the end of the workflow.</summary>
public sealed record ColumnNameRequest(string Name);

/// <summary>Reorder a project's columns (owner-only): the complete set of column ids in the new order.</summary>
public sealed record ReorderColumnsRequest(IReadOnlyList<Guid>? OrderedIds);

/// <summary>
/// Create a task (any project member). Goes to <see cref="ColumnId"/> if given (must belong to the
/// project), otherwise the first column — a new task starts in the first workflow state. The
/// assignee, when set, must be one of the project's assignees (enforced server-side).
/// </summary>
public sealed record CreateTaskRequest(
    string Title,
    string? Description,
    TaskPriority? Priority,
    string? AssigneeId,
    DateOnly? DueDate,
    IReadOnlyList<string>? Labels,
    Guid? ColumnId);

/// <summary>
/// Full-replace edit of a task (any project member). <see cref="ColumnId"/> is the task's status and
/// is required. Same assignee restriction as create.
/// </summary>
public sealed record UpdateTaskRequest(
    string Title,
    string? Description,
    TaskPriority? Priority,
    string? AssigneeId,
    DateOnly? DueDate,
    IReadOnlyList<string>? Labels,
    Guid ColumnId);

/// <summary>Drag-move a task to a column + position (any project member); changes its status.</summary>
public sealed record MoveTaskRequest(Guid ColumnId, int Position);

// ---------------------------------------------------------------------------
// Store results — outcome + payload, mapped to HTTP at the endpoint so the store
// stays transport-agnostic (mirrors the project store's result records).
// ---------------------------------------------------------------------------

/// <summary>Outcome of an owner-only column mutation, mapped to a status at the endpoint.</summary>
public enum ColumnOutcome
{
    /// <summary>The mutation succeeded.</summary>
    Ok,
    /// <summary>The caller can see the project but is not its owner — refuse (403).</summary>
    Forbidden,
    /// <summary>No visible project/column with that id — treat as 404 (no existence leak).</summary>
    NotFound,
    /// <summary>Delete refused because the column still holds tasks (409).</summary>
    NotEmpty,
    /// <summary>The request was structurally invalid (e.g. reorder ids don't match the columns) — 400.</summary>
    Invalid,
}

/// <summary>Result of a column mutation: outcome plus, on create, the new column (empty of tasks).
/// Rename/reorder/delete carry no body (the client already knows the result), so <see cref="Column"/>
/// is only set for create.</summary>
public sealed record ColumnResult(ColumnOutcome Outcome, BoardColumnResponse? Column = null, string? Error = null);

/// <summary>Outcome of a task mutation. Membership equals visibility, so there is no 403 for tasks.</summary>
public enum TaskOutcome
{
    /// <summary>The mutation succeeded.</summary>
    Ok,
    /// <summary>No visible project/task with that id — treat as 404.</summary>
    NotFound,
    /// <summary>The requested assignee is not one of the project's assignees — 400.</summary>
    InvalidAssignee,
    /// <summary>The target column doesn't belong to this project — 400.</summary>
    InvalidColumn,
}

/// <summary>Result of a task mutation: outcome plus, on success, the refreshed task read model.</summary>
public sealed record TaskResult(TaskOutcome Outcome, TaskResponse? Task = null, string? Error = null);
