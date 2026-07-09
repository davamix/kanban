namespace KanbanApi.Models;

/// <summary>
/// A column on a project's board. The ordered set of a project's columns *is* its task workflow
/// (e.g. <c>TODO → WIP → DONE</c>); <see cref="Position"/> gives that order, and a task's column is
/// its status. Every project is seeded with three default columns on creation. Columns are
/// structural, so add/rename/reorder/delete are owner-only (tasks are collaborative — see
/// <see cref="TaskItem"/>); read access follows the parent project's owner-or-assignee filter.
/// </summary>
public sealed class BoardColumn
{
    public Guid Id { get; set; }

    /// <summary>The project this column belongs to. Columns cascade-delete with the project.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    /// <summary>Zero-based position in the workflow. Contiguous per project; reordering rewrites it.</summary>
    public int Position { get; set; }

    /// <summary>Tasks currently in this column, ordered by their own <see cref="TaskItem.Position"/>.</summary>
    public ICollection<TaskItem> Tasks { get; } = new List<TaskItem>();
}
