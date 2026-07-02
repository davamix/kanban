using KanbanApi.Services;

namespace KanbanApi.Endpoints;

/// <summary>The user directory backing the assignee picker (Logto Management API).</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/users?search=foo — list/search Logto users for the assignee picker.
        app.MapGet("/api/users", async (ILogtoManagementClient logto, string? search) =>
            Results.Ok(await logto.GetUsersAsync(search)))
            .RequireAuthorization()
            .WithTags("Users")
            .WithSummary("List or search users (assignee directory)");
    }
}
