using KanbanApi.Data;
using KanbanApi.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Services;

/// <summary>
/// EF Core <see cref="IBoardStore"/>. Reads go through <see cref="KanbanDbContext"/>'s global query
/// filters (a project/column/task the caller can't see simply isn't returned — a <c>404</c>, never a
/// <c>403</c>), so this class never hand-writes a per-user <c>Where</c>. On top of that: column
/// mutations re-check <c>OwnerId</c> (owner-only), task mutations don't (membership = visibility), and
/// a task's assignee is validated against the project's assignees.
/// </summary>
public sealed class EfBoardStore(KanbanDbContext db, ICurrentUser currentUser) : IBoardStore
{
    /// <summary>The default workflow every project starts with (TODO → WIP → DONE).</summary>
    public static readonly string[] DefaultColumnNames = ["TODO", "WIP", "DONE"];

    /// <summary>Fresh default columns for a project, positioned in workflow order.</summary>
    public static IEnumerable<BoardColumn> DefaultColumns(Guid projectId) =>
        DefaultColumnNames.Select((name, i) => new BoardColumn
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            Position = i,
        });

    public async Task<BoardResponse?> GetBoardAsync(Guid projectId, CancellationToken ct = default)
    {
        var me = currentUser.Id;

        // The query filter scopes this to visible projects, so an id the caller can't see is null → 404.
        var project = await db.Projects.AsNoTracking()
            .Include(p => p.Assignees).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return null;

        // Backfill defaults for projects created before boards existed (new projects get them on create).
        if (!await db.BoardColumns.AnyAsync(c => c.ProjectId == projectId, ct))
        {
            db.BoardColumns.AddRange(DefaultColumns(projectId));
            await db.SaveChangesAsync(ct);
        }

        var columns = await db.BoardColumns.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Position)
            .Include(c => c.Tasks.OrderBy(t => t.Position)).ThenInclude(t => t.Assignee)
            .ToListAsync(ct);

        var assignees = project.Assignees
            .Select(a => new AssigneeSummary(a.UserId, a.User?.DisplayName))
            .ToList();

        var columnResponses = columns.Select(c => new BoardColumnResponse(
            c.Id, c.Name, c.Position,
            c.Tasks.Select(t => ToResponse(t, t.Assignee?.DisplayName)).ToList())).ToList();

        return new BoardResponse(project.Id, project.Name, project.OwnerId == me, assignees, columnResponses);
    }

    // --- Columns (owner-only) ------------------------------------------------

    public async Task<ColumnResult> CreateColumnAsync(Guid projectId, string name, CancellationToken ct = default)
    {
        var gate = await GateOwnerAsync(projectId, ct);
        if (gate is not null)
            return gate;

        var maxPos = await db.BoardColumns.Where(c => c.ProjectId == projectId)
            .Select(c => (int?)c.Position).MaxAsync(ct) ?? -1;
        var column = new BoardColumn
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name.Trim(),
            Position = maxPos + 1,
        };
        db.BoardColumns.Add(column);
        await db.SaveChangesAsync(ct);
        return new ColumnResult(ColumnOutcome.Ok, new BoardColumnResponse(column.Id, column.Name, column.Position, []));
    }

    public async Task<ColumnResult> RenameColumnAsync(Guid projectId, Guid columnId, string name, CancellationToken ct = default)
    {
        var gate = await GateOwnerAsync(projectId, ct);
        if (gate is not null)
            return gate;

        var column = await db.BoardColumns.FirstOrDefaultAsync(c => c.Id == columnId && c.ProjectId == projectId, ct);
        if (column is null)
            return new ColumnResult(ColumnOutcome.NotFound);

        column.Name = name.Trim();
        await db.SaveChangesAsync(ct);
        return new ColumnResult(ColumnOutcome.Ok);
    }

    public async Task<ColumnResult> ReorderColumnsAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        var gate = await GateOwnerAsync(projectId, ct);
        if (gate is not null)
            return gate;

        var columns = await db.BoardColumns.Where(c => c.ProjectId == projectId).ToListAsync(ct);
        var ids = orderedIds ?? [];
        var existing = columns.Select(c => c.Id).ToHashSet();
        // The new order must be a permutation of exactly the project's current columns.
        if (ids.Count != columns.Count || ids.Distinct().Count() != ids.Count || !ids.All(existing.Contains))
            return new ColumnResult(ColumnOutcome.Invalid, Error: "The ordered ids must be exactly the project's columns.");

        var byId = columns.ToDictionary(c => c.Id);
        for (var i = 0; i < ids.Count; i++)
            byId[ids[i]].Position = i;
        await db.SaveChangesAsync(ct);
        return new ColumnResult(ColumnOutcome.Ok);
    }

    public async Task<ColumnResult> DeleteColumnAsync(Guid projectId, Guid columnId, CancellationToken ct = default)
    {
        var gate = await GateOwnerAsync(projectId, ct);
        if (gate is not null)
            return gate;

        var column = await db.BoardColumns.Include(c => c.Tasks)
            .FirstOrDefaultAsync(c => c.Id == columnId && c.ProjectId == projectId, ct);
        if (column is null)
            return new ColumnResult(ColumnOutcome.NotFound);
        // Blocked while non-empty — the owner must move or delete the tasks first (no silent loss).
        if (column.Tasks.Count > 0)
            return new ColumnResult(ColumnOutcome.NotEmpty, Error: "Move or delete this column's tasks before deleting it.");

        db.BoardColumns.Remove(column);
        await db.SaveChangesAsync(ct);

        // Keep the remaining workflow positions contiguous.
        var remaining = await db.BoardColumns.Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Position).ToListAsync(ct);
        for (var i = 0; i < remaining.Count; i++)
            remaining[i].Position = i;
        await db.SaveChangesAsync(ct);
        return new ColumnResult(ColumnOutcome.Ok);
    }

    // Owner gate for column mutations: null project → NotFound (no existence leak), non-owner →
    // Forbidden, owner → null (proceed). Returns the failing result, or null to continue.
    private async Task<ColumnResult?> GateOwnerAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return new ColumnResult(ColumnOutcome.NotFound);
        if (project.OwnerId != currentUser.Id)
            return new ColumnResult(ColumnOutcome.Forbidden);
        return null;
    }

    // --- Tasks (any project member) ------------------------------------------

    public async Task<TaskResult> CreateTaskAsync(Guid projectId, CreateTaskRequest request, CancellationToken ct = default)
    {
        // Creator is the authenticated caller — never a request field (ASVS V8). Fail closed if unset.
        var me = currentUser.Id
            ?? throw new InvalidOperationException("Cannot create a task without a current user.");
        var project = await db.Projects.Include(p => p.Assignees).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return new TaskResult(TaskOutcome.NotFound);

        var columns = await EnsureColumnsAsync(projectId, ct);
        BoardColumn target;
        if (request.ColumnId is { } cid)
        {
            var found = columns.FirstOrDefault(c => c.Id == cid);
            if (found is null)
                return new TaskResult(TaskOutcome.InvalidColumn, Error: "Target column not found in this project.");
            target = found;
        }
        else
        {
            // No column given → the first workflow state (a new task starts there).
            target = columns[0];
        }

        if (!AssigneeAllowed(project, request.AssigneeId))
            return new TaskResult(TaskOutcome.InvalidAssignee, Error: "The assignee must be one of the project's assignees.");

        var maxPos = await db.Tasks.Where(t => t.ColumnId == target.Id)
            .Select(t => (int?)t.Position).MaxAsync(ct) ?? -1;
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ColumnId = target.Id,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Priority = request.Priority ?? TaskPriority.Medium,
            AssigneeId = request.AssigneeId,
            DueDate = request.DueDate,
            Labels = NormalizeLabels(request.Labels),
            Position = maxPos + 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = me,
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        return new TaskResult(TaskOutcome.Ok, ToResponse(task, AssigneeName(project, task.AssigneeId)));
    }

    public async Task<TaskResult> UpdateTaskAsync(Guid projectId, Guid taskId, UpdateTaskRequest request, CancellationToken ct = default)
    {
        var project = await db.Projects.Include(p => p.Assignees).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return new TaskResult(TaskOutcome.NotFound);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.ProjectId == projectId, ct);
        if (task is null)
            return new TaskResult(TaskOutcome.NotFound);

        var columns = await EnsureColumnsAsync(projectId, ct);
        if (columns.All(c => c.Id != request.ColumnId))
            return new TaskResult(TaskOutcome.InvalidColumn, Error: "Target column not found in this project.");
        if (!AssigneeAllowed(project, request.AssigneeId))
            return new TaskResult(TaskOutcome.InvalidAssignee, Error: "The assignee must be one of the project's assignees.");

        // Changing the column via the form is a status change: append to the end of the target column.
        if (task.ColumnId != request.ColumnId)
        {
            var maxPos = await db.Tasks.Where(t => t.ColumnId == request.ColumnId)
                .Select(t => (int?)t.Position).MaxAsync(ct) ?? -1;
            task.ColumnId = request.ColumnId;
            task.Position = maxPos + 1;
        }

        task.Title = request.Title.Trim();
        task.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        task.Priority = request.Priority ?? TaskPriority.Medium;
        task.AssigneeId = request.AssigneeId;
        task.DueDate = request.DueDate;
        task.Labels = NormalizeLabels(request.Labels);
        await db.SaveChangesAsync(ct);
        return new TaskResult(TaskOutcome.Ok, ToResponse(task, AssigneeName(project, task.AssigneeId)));
    }

    public async Task<TaskResult> MoveTaskAsync(Guid projectId, Guid taskId, MoveTaskRequest request, CancellationToken ct = default)
    {
        var project = await db.Projects.Include(p => p.Assignees).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return new TaskResult(TaskOutcome.NotFound);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.ProjectId == projectId, ct);
        if (task is null)
            return new TaskResult(TaskOutcome.NotFound);
        if (!await db.BoardColumns.AnyAsync(c => c.Id == request.ColumnId && c.ProjectId == projectId, ct))
            return new TaskResult(TaskOutcome.InvalidColumn, Error: "Target column not found in this project.");

        var oldColumnId = task.ColumnId;

        // Reindex the target column with the moved task inserted at the requested (clamped) position.
        var targetTasks = await db.Tasks.Where(t => t.ColumnId == request.ColumnId && t.Id != taskId)
            .OrderBy(t => t.Position).ToListAsync(ct);
        task.ColumnId = request.ColumnId;
        var index = Math.Clamp(request.Position, 0, targetTasks.Count);
        targetTasks.Insert(index, task);
        for (var i = 0; i < targetTasks.Count; i++)
            targetTasks[i].Position = i;

        // If it left another column, close the gap there too.
        if (oldColumnId != request.ColumnId)
        {
            var sourceTasks = await db.Tasks.Where(t => t.ColumnId == oldColumnId && t.Id != taskId)
                .OrderBy(t => t.Position).ToListAsync(ct);
            for (var i = 0; i < sourceTasks.Count; i++)
                sourceTasks[i].Position = i;
        }

        await db.SaveChangesAsync(ct);
        return new TaskResult(TaskOutcome.Ok, ToResponse(task, AssigneeName(project, task.AssigneeId)));
    }

    public async Task<TaskOutcome> DeleteTaskAsync(Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.ProjectId == projectId, ct);
        if (task is null)
            return TaskOutcome.NotFound;

        var columnId = task.ColumnId;
        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);

        // Keep the column's remaining task positions contiguous.
        var siblings = await db.Tasks.Where(t => t.ColumnId == columnId)
            .OrderBy(t => t.Position).ToListAsync(ct);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].Position = i;
        await db.SaveChangesAsync(ct);
        return TaskOutcome.Ok;
    }

    // --- Helpers -------------------------------------------------------------

    // Columns for a project, seeding the defaults if it has none (keeps callers robust for
    // pre-board projects). Returns them tracked and ordered by position.
    private async Task<List<BoardColumn>> EnsureColumnsAsync(Guid projectId, CancellationToken ct)
    {
        var columns = await db.BoardColumns.Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Position).ToListAsync(ct);
        if (columns.Count == 0)
        {
            columns = DefaultColumns(projectId).ToList();
            db.BoardColumns.AddRange(columns);
            await db.SaveChangesAsync(ct);
        }
        return columns;
    }

    // A task's assignee, when set, must be one of the project's assignees (restriction shared by
    // create + update). The project must be loaded with its Assignees.
    private static bool AssigneeAllowed(Project project, string? assigneeId) =>
        assigneeId is null || project.Assignees.Any(a => a.UserId == assigneeId);

    private static string? AssigneeName(Project project, string? assigneeId) =>
        assigneeId is null ? null : project.Assignees.FirstOrDefault(a => a.UserId == assigneeId)?.User?.DisplayName;

    // Trim, drop blanks, de-duplicate (case-insensitively), and cap the count so a card's label row
    // stays bounded.
    private static List<string> NormalizeLabels(IReadOnlyList<string>? labels) =>
        (labels ?? [])
        .Select(l => l?.Trim() ?? string.Empty)
        .Where(l => l.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToList();

    private static TaskResponse ToResponse(TaskItem t, string? assigneeName) => new(
        t.Id, t.ColumnId, t.Title, t.Description, t.Priority,
        t.AssigneeId, assigneeName, t.DueDate, t.Labels, t.Position);
}
