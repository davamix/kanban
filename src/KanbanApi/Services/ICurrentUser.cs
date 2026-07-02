namespace KanbanApi.Services;

/// <summary>
/// The authenticated user for the current request, resolved from the validated principal
/// regardless of scheme (BFF cookie or JWT bearer). The single place identity is derived —
/// owner/assignee values are taken from here, never from request payloads.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The Logto <c>sub</c> — the stable, never-reassigned user identifier.</summary>
    string? Id { get; }

    string? Email { get; }
    string? DisplayName { get; }
}
