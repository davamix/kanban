namespace KanbanApi.Models;

/// <summary>Task priority, shown on the card and set from the task form. Default is <see cref="Medium"/>.</summary>
public enum TaskPriority
{
    Low,
    Medium,
    High,
    Urgent,
}

/// <summary>
/// A unit of work on a project's board. Named <c>TaskItem</c> to avoid colliding with
/// <see cref="System.Threading.Tasks.Task"/>. A task's <see cref="ColumnId"/> is its status; moving
/// it between columns changes that status, and <see cref="Position"/> orders it within its column
/// (drag-to-reorder). Task CRUD/move is open to any project member (owner or assignee); the
/// <see cref="AssigneeId"/> is restricted to the project's assignees. Read/visibility follows the
/// parent project's owner-or-assignee query filter — see <see cref="Data.KanbanDbContext"/>.
/// </summary>
public sealed class TaskItem
{
    public Guid Id { get; set; }

    /// <summary>Denormalised parent project (also reachable via the column) so the visibility
    /// filter and the assignee-restriction check don't have to join through the column.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The column the task sits in — i.e. its current status in the workflow.</summary>
    public Guid ColumnId { get; set; }
    public BoardColumn Column { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    /// <summary>
    /// Optional single assignee (Logto <c>sub</c>). MUST be one of the project's assignees — enforced
    /// server-side in the store. Null = unassigned.
    /// </summary>
    public string? AssigneeId { get; set; }
    public AppUser? Assignee { get; set; }

    public DateOnly? DueDate { get; set; }

    /// <summary>Free-text classification labels shown on the card. Stored as a Postgres <c>text[]</c>.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Zero-based order within the column (drag-to-reorder). Contiguous per column.</summary>
    public int Position { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
