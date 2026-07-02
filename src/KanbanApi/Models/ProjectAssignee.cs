namespace KanbanApi.Models;

/// <summary>Join row: a user assigned to a project (composite key Project+User).</summary>
public sealed class ProjectAssignee
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The assignee's Logto <c>sub</c>.</summary>
    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;
}
