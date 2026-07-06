using KanbanApi.Models;
using KanbanApi.Services;

namespace KanbanApi.Endpoints;

/// <summary>
/// The project REST surface: list the projects the signed-in user owns or is assigned to, create a
/// new project (the caller becomes the owner), and delete a project (owner-only). Update and
/// assignee management arrive with later screens. The whole group requires authentication; per-user
/// read isolation is enforced by the store's global query filter, and the owner is always taken from
/// the session — delete additionally re-checks ownership before removing.
/// </summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();

        // GET /api/projects — projects visible to the caller (owner or assignee), newest first.
        group.MapGet("/", async (IProjectStore store, CancellationToken ct) =>
            Results.Ok(await store.ListVisibleAsync(ct)))
            .WithSummary("List the projects the current user owns or is assigned to");

        // POST /api/projects — create a project owned by the caller. Validates shape here (RFC 9457
        // problem details); the store enforces owner-from-session and assignee resolution.
        group.MapPost("/", async (CreateProjectRequest request, IProjectStore store, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                errors["name"] = ["Project name is required."];
            else if (name.Length > 200)
                errors["name"] = ["Project name must be 200 characters or fewer."];
            if (request.Description is { Length: > 2000 })
                errors["description"] = ["Description must be 2000 characters or fewer."];
            if (request.StartDate is null)
                errors["startDate"] = ["Start date is required."];
            if (request.EndDate is null)
                errors["endDate"] = ["End date is required."];
            else if (request.StartDate is { } start && request.EndDate is { } end && end < start)
                errors["endDate"] = ["End date must be on or after the start date."];
            if (request.Budget is < 0)
                errors["budget"] = ["Budget cannot be negative."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var created = await store.CreateAsync(request, ct);
            return Results.Created($"/api/projects/{created.Id}", created);
        })
            .WithSummary("Create a project owned by the current user");

        // DELETE /api/projects/{id} — remove a project. Owner-only; the store scopes the lookup to
        // visible projects (so a hidden id is a 404, not a 403 that would confirm existence) and
        // gates the removal on ownership. Antiforgery is enforced for the cookie/BFF path upstream.
        group.MapDelete("/{id:guid}", async (Guid id, IProjectStore store, CancellationToken ct) =>
        {
            var outcome = await store.DeleteAsync(id, ct);
            return outcome switch
            {
                ProjectDeleteOutcome.Deleted => Results.NoContent(),
                ProjectDeleteOutcome.Forbidden => Results.Problem(
                    title: "Forbidden",
                    detail: "Only the project owner can delete this project.",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Problem(
                    title: "Not Found",
                    detail: "Project not found.",
                    statusCode: StatusCodes.Status404NotFound),
            };
        })
            .WithSummary("Delete a project (owner only)");
    }
}
