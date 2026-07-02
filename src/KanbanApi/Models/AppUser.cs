namespace KanbanApi.Models;

/// <summary>
/// Local projection of a Logto user, keyed by the Logto <c>sub</c>. Upserted on first login
/// and when a user is referenced as a project/task assignee, so owner/assignee references are
/// FK-backed and the UI can show names/emails without re-querying Logto on every render.
/// </summary>
public sealed class AppUser
{
    /// <summary>The Logto <c>sub</c>.</summary>
    public string Id { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
}
