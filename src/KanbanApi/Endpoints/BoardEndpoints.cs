using KanbanApi.Models;
using KanbanApi.Services;

namespace KanbanApi.Endpoints;

/// <summary>
/// The board REST surface, nested under a project: read the board, manage its columns (owner-only),
/// and manage its tasks (any project member). The whole group requires authentication; visibility and
/// the owner/member split are enforced in <see cref="IBoardStore"/> (via the DbContext query filters
/// plus an owner re-check on column mutations). Shape is validated here as RFC 9457 problem details;
/// the project/column/task subject always comes from the route + session, never a spoofable payload.
/// </summary>
public static class BoardEndpoints
{
    public static void MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}")
            .WithTags("Board").RequireAuthorization();

        // GET …/board — the whole board (columns + tasks) for a visible project.
        group.MapGet("/board", async (Guid projectId, IBoardStore store, CancellationToken ct) =>
        {
            var board = await store.GetBoardAsync(projectId, ct);
            return board is null ? NotFound("Project not found.") : Results.Ok(board);
        })
            .WithSummary("Get a project's board (columns and tasks)");

        // --- Columns (owner-only) --------------------------------------------

        group.MapPost("/columns", async (Guid projectId, ColumnNameRequest request, IBoardStore store, CancellationToken ct) =>
        {
            var errors = ValidateColumnName(request.Name);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var result = await store.CreateColumnAsync(projectId, request.Name, ct);
            return result.Outcome == ColumnOutcome.Ok
                ? Results.Created($"/api/projects/{projectId}/columns/{result.Column!.Id}", result.Column)
                : MapColumn(result);
        })
            .WithSummary("Add a board column (owner only)");

        group.MapPut("/columns/order", async (Guid projectId, ReorderColumnsRequest request, IBoardStore store, CancellationToken ct) =>
        {
            var result = await store.ReorderColumnsAsync(projectId, request.OrderedIds ?? [], ct);
            return result.Outcome == ColumnOutcome.Ok ? Results.NoContent() : MapColumn(result);
        })
            .WithSummary("Reorder board columns / the workflow (owner only)");

        group.MapPut("/columns/{columnId:guid}", async (Guid projectId, Guid columnId, ColumnNameRequest request, IBoardStore store, CancellationToken ct) =>
        {
            var errors = ValidateColumnName(request.Name);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var result = await store.RenameColumnAsync(projectId, columnId, request.Name, ct);
            return result.Outcome == ColumnOutcome.Ok ? Results.NoContent() : MapColumn(result);
        })
            .WithSummary("Rename a board column (owner only)");

        group.MapDelete("/columns/{columnId:guid}", async (Guid projectId, Guid columnId, IBoardStore store, CancellationToken ct) =>
        {
            var result = await store.DeleteColumnAsync(projectId, columnId, ct);
            return result.Outcome == ColumnOutcome.Ok ? Results.NoContent() : MapColumn(result);
        })
            .WithSummary("Delete an empty board column (owner only)");

        // --- Tasks (any project member) --------------------------------------

        group.MapPost("/tasks", async (Guid projectId, CreateTaskRequest request, IBoardStore store, CancellationToken ct) =>
        {
            var errors = ValidateTask(request.Title, request.Description, request.Labels);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var result = await store.CreateTaskAsync(projectId, request, ct);
            return result.Outcome == TaskOutcome.Ok
                ? Results.Created($"/api/projects/{projectId}/tasks/{result.Task!.Id}", result.Task)
                : MapTask(result);
        })
            .WithSummary("Create a task (any project member)");

        group.MapPut("/tasks/{taskId:guid}", async (Guid projectId, Guid taskId, UpdateTaskRequest request, IBoardStore store, CancellationToken ct) =>
        {
            var errors = ValidateTask(request.Title, request.Description, request.Labels);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var result = await store.UpdateTaskAsync(projectId, taskId, request, ct);
            return result.Outcome == TaskOutcome.Ok ? Results.Ok(result.Task) : MapTask(result);
        })
            .WithSummary("Update a task (any project member)");

        group.MapPut("/tasks/{taskId:guid}/move", async (Guid projectId, Guid taskId, MoveTaskRequest request, IBoardStore store, CancellationToken ct) =>
        {
            var result = await store.MoveTaskAsync(projectId, taskId, request, ct);
            return result.Outcome == TaskOutcome.Ok ? Results.Ok(result.Task) : MapTask(result);
        })
            .WithSummary("Move a task to a column/position (any project member)");

        group.MapDelete("/tasks/{taskId:guid}", async (Guid projectId, Guid taskId, IBoardStore store, CancellationToken ct) =>
        {
            var outcome = await store.DeleteTaskAsync(projectId, taskId, ct);
            return outcome == TaskOutcome.Ok ? Results.NoContent() : NotFound("Task not found.");
        })
            .WithSummary("Delete a task (any project member)");
    }

    // Map a non-Ok column outcome to its HTTP status (RFC 9457).
    private static IResult MapColumn(ColumnResult result) => result.Outcome switch
    {
        ColumnOutcome.Forbidden => Results.Problem(
            title: "Forbidden",
            detail: "Only the project owner can manage board columns.",
            statusCode: StatusCodes.Status403Forbidden),
        ColumnOutcome.NotEmpty => Results.Problem(
            title: "Conflict",
            detail: result.Error ?? "The column still contains tasks.",
            statusCode: StatusCodes.Status409Conflict),
        ColumnOutcome.Invalid => Results.Problem(
            title: "Bad Request",
            detail: result.Error ?? "The request was invalid.",
            statusCode: StatusCodes.Status400BadRequest),
        _ => NotFound("Project or column not found."),
    };

    // Map a non-Ok task outcome to its HTTP status. Semantic 400s are keyed to the payload field so
    // the SPA can surface them inline like every other validation error.
    private static IResult MapTask(TaskResult result) => result.Outcome switch
    {
        TaskOutcome.InvalidAssignee => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["assigneeId"] = [result.Error ?? "Invalid assignee."] }),
        TaskOutcome.InvalidColumn => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["columnId"] = [result.Error ?? "Invalid column."] }),
        _ => NotFound("Project or task not found."),
    };

    private static IResult NotFound(string detail) => Results.Problem(
        title: "Not Found", detail: detail, statusCode: StatusCodes.Status404NotFound);

    private static Dictionary<string, string[]> ValidateColumnName(string? name)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            errors["name"] = ["Column name is required."];
        else if (trimmed.Length > 100)
            errors["name"] = ["Column name must be 100 characters or fewer."];
        return errors;
    }

    // Shape validation shared by task create (POST) and edit (PUT). Priority binds as an enum, so an
    // out-of-range value is rejected by model binding before this runs; the column/assignee semantic
    // checks live in the store (they need the project's data).
    private static Dictionary<string, string[]> ValidateTask(
        string? title, string? description, IReadOnlyList<string>? labels)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            errors["title"] = ["Task title is required."];
        else if (trimmed.Length > 200)
            errors["title"] = ["Task title must be 200 characters or fewer."];
        if (description is { Length: > 2000 })
            errors["description"] = ["Description must be 2000 characters or fewer."];
        if (labels is not null)
        {
            if (labels.Count > 20)
                errors["labels"] = ["A task can have at most 20 labels."];
            else if (labels.Any(l => (l?.Trim().Length ?? 0) > 50))
                errors["labels"] = ["Each label must be 50 characters or fewer."];
        }
        return errors;
    }
}
