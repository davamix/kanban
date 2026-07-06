using KanbanApi.Models;

namespace KanbanApi.Services;

/// <summary>
/// Read access to projects for the current user. Reads are isolated to the current user (owner or
/// assignee) by the DbContext global query filter; the store maps entities to the
/// <see cref="ProjectResponse"/> read model, resolving each project's assignees to display names
/// and the caller's own relationship (owner vs assignee). Create/update/assignee mutations are
/// added with the project-creation screen.
/// </summary>
public interface IProjectStore
{
    /// <summary>
    /// Projects the current user owns or is assigned to, newest first. Returns an empty list when
    /// the caller has no visible projects (or no current user — fail closed).
    /// </summary>
    Task<IReadOnlyList<ProjectResponse>> ListVisibleAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a project owned by the current user, who is always added as an assignee. Requested
    /// assignees are validated against the Logto directory (unknown ids are dropped) and mirrored
    /// into the local users table so the FK holds and cards can show names. Returns the created
    /// project as it should appear on the selection screen (the caller is the owner).
    /// </summary>
    Task<ProjectResponse> CreateAsync(CreateProjectRequest request, CancellationToken ct = default);

    /// <summary>
    /// Updates a project. Edit is owner-only: the global query filter first scopes lookup to
    /// projects the caller can see (owner or assignee), then the owner check gates the mutation so an
    /// assignee cannot edit a project they merely share. Scalar fields are replaced; the assignee set
    /// is reconciled (unknown ids dropped, the owner always kept). Returns the outcome plus the
    /// refreshed read model on success, so the endpoint can map it to 200 / 403 / 404 without leaking
    /// existence.
    /// </summary>
    Task<ProjectUpdateResult> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes a project. Delete is owner-only: the global query filter first scopes lookup to
    /// projects the caller can see (owner or assignee), then the owner check gates the removal so an
    /// assignee cannot delete a project they merely share. Assignees cascade with the project.
    /// Returns the outcome so the endpoint can map it to 204 / 403 / 404 without leaking existence.
    /// </summary>
    Task<ProjectDeleteOutcome> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Result of an update attempt: the outcome plus, on success, the refreshed read model for the
/// client to re-render the card without a follow-up fetch.
/// </summary>
public sealed record ProjectUpdateResult(ProjectUpdateOutcome Outcome, ProjectResponse? Project);

/// <summary>Result of an update attempt, mapped to an HTTP status at the endpoint.</summary>
public enum ProjectUpdateOutcome
{
    /// <summary>The project was updated.</summary>
    Updated,
    /// <summary>The caller can see the project but is not its owner — refuse (403).</summary>
    Forbidden,
    /// <summary>No visible project with that id (absent, or not owned/assigned) — treat as 404.</summary>
    NotFound,
}

/// <summary>Result of a delete attempt, mapped to an HTTP status at the endpoint.</summary>
public enum ProjectDeleteOutcome
{
    /// <summary>The project was deleted.</summary>
    Deleted,
    /// <summary>The caller can see the project but is not its owner — refuse (403).</summary>
    Forbidden,
    /// <summary>No visible project with that id (absent, or not owned/assigned) — treat as 404.</summary>
    NotFound,
}
