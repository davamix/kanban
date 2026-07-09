using KanbanApi.Models;

namespace KanbanApi.Services;

/// <summary>
/// Read/write access to a project's board (columns + tasks) for the current user. Every operation is
/// scoped by the DbContext global query filters (a project/column/task the caller can't see is a
/// <c>404</c>, never a <c>403</c> that would confirm existence). Two authorization tiers sit on top:
/// <list type="bullet">
///   <item>Columns are structural workflow — add/rename/reorder/delete are <b>owner-only</b>.</item>
///   <item>Tasks are collaborative — create/edit/move/delete are open to any project <b>member</b>
///   (owner or assignee). A task's assignee is restricted to the project's assignees.</item>
/// </list>
/// The subject always comes from <see cref="ICurrentUser"/>, never a payload (ASVS V8).
/// </summary>
public interface IBoardStore
{
    /// <summary>
    /// The full board for a project the caller can see (heading, ownership flag, the project's
    /// assignees, and ordered columns each with their ordered tasks), or null when no visible project
    /// has that id. Seeds the default columns lazily if the project somehow has none.
    /// </summary>
    Task<BoardResponse?> GetBoardAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Append a column to the workflow (owner-only).</summary>
    Task<ColumnResult> CreateColumnAsync(Guid projectId, string name, CancellationToken ct = default);

    /// <summary>Rename a column (owner-only).</summary>
    Task<ColumnResult> RenameColumnAsync(Guid projectId, Guid columnId, string name, CancellationToken ct = default);

    /// <summary>Reorder all of a project's columns — the new workflow order (owner-only). The ids must
    /// be exactly the project's current columns, else <see cref="ColumnOutcome.Invalid"/>.</summary>
    Task<ColumnResult> ReorderColumnsAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);

    /// <summary>Delete a column (owner-only). Refused with <see cref="ColumnOutcome.NotEmpty"/> while it
    /// still holds tasks — the owner must move or delete them first.</summary>
    Task<ColumnResult> DeleteColumnAsync(Guid projectId, Guid columnId, CancellationToken ct = default);

    /// <summary>Create a task in the given column (or the first column if none given) — any member.</summary>
    Task<TaskResult> CreateTaskAsync(Guid projectId, CreateTaskRequest request, CancellationToken ct = default);

    /// <summary>Full-replace edit of a task, including its column/status — any member.</summary>
    Task<TaskResult> UpdateTaskAsync(Guid projectId, Guid taskId, UpdateTaskRequest request, CancellationToken ct = default);

    /// <summary>Move a task to a column + position (drag) — any member.</summary>
    Task<TaskResult> MoveTaskAsync(Guid projectId, Guid taskId, MoveTaskRequest request, CancellationToken ct = default);

    /// <summary>Delete a task — any member. Returns whether the task was found/removed.</summary>
    Task<TaskOutcome> DeleteTaskAsync(Guid projectId, Guid taskId, CancellationToken ct = default);
}
