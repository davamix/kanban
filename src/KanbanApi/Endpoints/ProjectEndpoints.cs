using KanbanApi.Services;

namespace KanbanApi.Endpoints;

/// <summary>
/// The project REST surface. For the project-selection screen this is the read side: list the
/// projects the signed-in user owns or is assigned to. Create/update/assignee management arrive
/// with the project-creation screen. The whole group requires authentication; per-user isolation
/// is enforced by the store's global query filter.
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
    }
}
