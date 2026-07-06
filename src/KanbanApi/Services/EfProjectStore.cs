using KanbanApi.Data;
using KanbanApi.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Services;

/// <summary>
/// EF Core <see cref="IProjectStore"/>. All reads go through <see cref="KanbanDbContext"/>'s global
/// query filter (owner-or-assignee), so this class never hand-writes a per-user <c>Where</c> —
/// isolation is enforced one layer down and fails closed when no user is set.
/// </summary>
public sealed class EfProjectStore(
    KanbanDbContext db, ICurrentUser currentUser, ILogtoManagementClient directory) : IProjectStore
{
    public async Task<IReadOnlyList<ProjectResponse>> ListVisibleAsync(CancellationToken ct = default)
    {
        var me = currentUser.Id;

        // The query filter already restricts this to the caller's owned/assigned projects.
        var projects = await db.Projects
            .AsNoTracking()
            .Include(p => p.Assignees).ThenInclude(a => a.User)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return projects.Select(p => new ProjectResponse(
            p.Id,
            p.Name,
            p.Description,
            p.StartDate,
            p.EndDate,
            p.Budget,
            p.OwnerId,
            IsOwner: p.OwnerId == me,
            Role: p.OwnerId == me ? "owner" : "assignee",
            Assignees: p.Assignees
                .Select(a => new AssigneeSummary(a.UserId, a.User?.DisplayName))
                .ToList()))
            .ToList();
    }

    public async Task<ProjectResponse> CreateAsync(CreateProjectRequest request, CancellationToken ct = default)
    {
        // Owner is the authenticated caller — never a request field (ASVS V8). Fail closed if unset.
        var me = currentUser.Id
            ?? throw new InvalidOperationException("Cannot create a project without a current user.");

        var (assigneeIds, directorySnapshot) = await ResolveAssigneesAsync(request.AssigneeIds, me, ct);

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            // Non-null by validation at the endpoint (both dates are required before we get here).
            StartDate = request.StartDate!.Value,
            EndDate = request.EndDate!.Value,
            Budget = request.Budget,
            OwnerId = me,
            CreatedAt = now,
            CreatedBy = me,
        };
        foreach (var id in assigneeIds)
            project.Assignees.Add(new ProjectAssignee { ProjectId = project.Id, UserId = id });

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return OwnerResponse(project, assigneeIds, directorySnapshot, me);
    }

    // Resolve requested assignees against the Logto directory (dropping unknown ids), always include
    // the owner (who may not appear in the snapshot), and mirror each into the local users table (FK
    // target), enriching names/emails from the directory so cards render without re-querying Logto.
    // Returns the desired id list (distinct, order-independent) plus the directory snapshot for naming.
    private async Task<(List<string> Ids, Dictionary<string, DirectoryUser> Directory)> ResolveAssigneesAsync(
        IReadOnlyList<string>? requestedIds, string me, CancellationToken ct)
    {
        var users = (await directory.GetUsersAsync(null, ct)).ToDictionary(u => u.Id);
        var ids = (requestedIds ?? [])
            .Where(users.ContainsKey)
            .Append(me)
            .Distinct()
            .ToList();

        foreach (var id in ids)
        {
            users.TryGetValue(id, out var dir);
            var existing = await db.Users.FindAsync([id], ct);   // FindAsync hits the tracker first.
            if (existing is null)
                db.Users.Add(new AppUser { Id = id, DisplayName = dir?.Name ?? (id == me ? currentUser.DisplayName : null), Email = dir?.Email ?? (id == me ? currentUser.Email : null) });
            else
            {
                existing.DisplayName ??= dir?.Name ?? (id == me ? currentUser.DisplayName : null);
                existing.Email ??= dir?.Email ?? (id == me ? currentUser.Email : null);
            }
        }
        return (ids, users);
    }

    // The owner's read model for a just-created/updated project: the caller is always the owner, so
    // IsOwner/Role are fixed. Assignee names come from the directory snapshot, falling back to the
    // caller's own claims for the owner (who may be absent from the directory).
    private ProjectResponse OwnerResponse(
        Project project, IEnumerable<string> assigneeIds, IReadOnlyDictionary<string, DirectoryUser> directorySnapshot, string me)
    {
        var assignees = assigneeIds
            .Select(id => new AssigneeSummary(id,
                directorySnapshot.TryGetValue(id, out var dir) ? dir.Name : (id == me ? currentUser.DisplayName : null)))
            .ToList();

        return new ProjectResponse(
            project.Id, project.Name, project.Description, project.StartDate, project.EndDate,
            project.Budget, me, IsOwner: true, Role: "owner", assignees);
    }

    public async Task<ProjectUpdateResult> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken ct = default)
    {
        // Owner is the authenticated caller — never a request field (ASVS V8). Fail closed if unset.
        var me = currentUser.Id
            ?? throw new InvalidOperationException("Cannot update a project without a current user.");

        // The global query filter scopes this to projects the caller can see (owner or assignee), so
        // an id they have no access to is simply "not found" — no existence leak (ASVS V8). Track it
        // (with its assignees) so we can mutate in place.
        var project = await db.Projects
            .Include(p => p.Assignees)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null)
            return new ProjectUpdateResult(ProjectUpdateOutcome.NotFound, null);

        // Edit is owner-only: an assignee can read the project but must not modify it.
        if (project.OwnerId != me)
            return new ProjectUpdateResult(ProjectUpdateOutcome.Forbidden, null);

        var (desiredIds, directorySnapshot) = await ResolveAssigneesAsync(request.AssigneeIds, me, ct);

        project.Name = request.Name.Trim();
        project.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        // Non-null by validation at the endpoint (both dates are required before we get here).
        project.StartDate = request.StartDate!.Value;
        project.EndDate = request.EndDate!.Value;
        project.Budget = request.Budget;

        // Reconcile assignees: drop those no longer desired (join rows delete via the required FK),
        // add the newly-selected ones. The owner is always in desiredIds, so it can never be dropped.
        var currentIds = project.Assignees.Select(a => a.UserId).ToHashSet();
        foreach (var a in project.Assignees.Where(a => !desiredIds.Contains(a.UserId)).ToList())
            project.Assignees.Remove(a);
        foreach (var uid in desiredIds.Where(uid => !currentIds.Contains(uid)))
            project.Assignees.Add(new ProjectAssignee { ProjectId = project.Id, UserId = uid });

        await db.SaveChangesAsync(ct);

        var response = OwnerResponse(project, desiredIds, directorySnapshot, me);
        return new ProjectUpdateResult(ProjectUpdateOutcome.Updated, response);
    }

    public async Task<ProjectDeleteOutcome> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var me = currentUser.Id;

        // The global query filter scopes this to projects the caller can see (owner or assignee),
        // so an id they have no access to is simply "not found" — no existence leak (ASVS V8).
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null)
            return ProjectDeleteOutcome.NotFound;

        // Delete is owner-only: an assignee can read the project but must not remove it.
        if (project.OwnerId != me)
            return ProjectDeleteOutcome.Forbidden;

        db.Projects.Remove(project);   // project_assignees rows cascade (FK OnDelete: Cascade).
        await db.SaveChangesAsync(ct);
        return ProjectDeleteOutcome.Deleted;
    }
}
