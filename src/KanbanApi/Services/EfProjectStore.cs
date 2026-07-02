using KanbanApi.Data;
using KanbanApi.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Services;

/// <summary>
/// EF Core <see cref="IProjectStore"/>. All reads go through <see cref="KanbanDbContext"/>'s global
/// query filter (owner-or-assignee), so this class never hand-writes a per-user <c>Where</c> —
/// isolation is enforced one layer down and fails closed when no user is set.
/// </summary>
public sealed class EfProjectStore(KanbanDbContext db, ICurrentUser currentUser) : IProjectStore
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
            p.OwnerId,
            IsOwner: p.OwnerId == me,
            Role: p.OwnerId == me ? "owner" : "assignee",
            Assignees: p.Assignees
                .Select(a => new AssigneeSummary(a.UserId, a.User?.DisplayName))
                .ToList()))
            .ToList();
    }
}
