using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KanbanApi.Models;

namespace KanbanApi.Services;

/// <summary>
/// Mirrors a newly-created Kanban project into the Calendar app, on-behalf-of its owner. This is a
/// best-effort side effect of project creation: it MUST NOT throw and MUST NOT block the Kanban
/// project from being created — every failure degrades to <see cref="ProjectMirrorStatus.Failed"/>
/// (or <see cref="ProjectMirrorStatus.Skipped"/> when unconfigured). Tasks are never mirrored.
/// See docs/ecosystem-integration.md §6.
/// </summary>
public interface ICalendarMirror
{
    Task<(ProjectMirrorStatus Status, string? CalendarProjectId)> MirrorProjectAsync(
        Project project, IReadOnlyList<string> assigneeIds, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ICalendarMirror"/> against Calendar's public REST API. Acquires an on-behalf-of token
/// (RFC 8693) for Calendar's audience via <see cref="ILogtoTokenExchange"/>, then creates the
/// Calendar project and adds each additional assignee. Returns <see cref="ProjectMirrorStatus.Skipped"/>
/// when the Calendar base URL or the exchange client is unset (feature off), so standalone dev is
/// unaffected. Everything is wrapped so no error escapes.
/// </summary>
public sealed class CalendarMirror(
    HttpClient http, IConfiguration config, ILogtoTokenExchange tokenExchange,
    ILogger<CalendarMirror> logger) : ICalendarMirror
{
    public async Task<(ProjectMirrorStatus Status, string? CalendarProjectId)> MirrorProjectAsync(
        Project project, IReadOnlyList<string> assigneeIds, CancellationToken ct = default)
    {
        var baseUrl = config["Calendar:Api:BaseUrl"];
        var resource = config["Calendar:Api:Resource"];
        // Both the endpoint and Calendar's audience are configuration (no audience is baked into code —
        // it differs per ecosystem/deployment). Either unset ⇒ mirroring is off for this deployment.
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(resource))
            return (ProjectMirrorStatus.Skipped, null);

        // One overall deadline for the *entire* on-behalf-of sequence (subject-token mint → exchange →
        // Calendar create + assignees), which spans three other typed HttpClients with their own default
        // timeouts. Bounding it here keeps a slow/unreachable Logto or Calendar from holding the inline
        // create request open. Cancellation degrades to Failed via the catch below.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var linked = timeout.Token;

        try
        {
            // Owner comes from the token's `sub`, so we exchange for a token carrying the owner's sub.
            var token = await tokenExchange.GetOnBehalfOfTokenAsync(project.OwnerId, resource, linked);
            if (token is null)
                return (ProjectMirrorStatus.Skipped, null);   // exchange not configured / unavailable

            var root = baseUrl.TrimEnd('/');
            var createBody = new CalendarProjectRequest(
                project.Name,
                project.Description,
                project.StartDate.ToString("yyyy-MM-dd"),
                project.EndDate.ToString("yyyy-MM-dd"));

            using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{root}/api/projects")
            {
                Content = JsonContent.Create(createBody),
            };
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var createRes = await http.SendAsync(createReq, linked);
            if (!createRes.IsSuccessStatusCode)
            {
                logger.LogWarning("Calendar mirror: create returned {Status} for project {ProjectId}.", (int)createRes.StatusCode, project.Id);
                return (ProjectMirrorStatus.Failed, null);
            }

            var created = await createRes.Content.ReadFromJsonAsync<CalendarProjectResponse>(cancellationToken: linked);
            if (string.IsNullOrWhiteSpace(created?.Id))
            {
                logger.LogWarning("Calendar mirror: create for project {ProjectId} returned no id.", project.Id);
                return (ProjectMirrorStatus.Failed, null);
            }

            // Add every assignee other than the owner (Calendar auto-adds the owner from the token sub).
            // An assignee failure doesn't undo the created project — we still record it as Mirrored.
            foreach (var userId in assigneeIds.Where(id => id != project.OwnerId))
            {
                using var assigneeReq = new HttpRequestMessage(HttpMethod.Post, $"{root}/api/projects/{created.Id}/assignees")
                {
                    Content = JsonContent.Create(new { userId }),
                };
                assigneeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var assigneeRes = await http.SendAsync(assigneeReq, linked);
                if (!assigneeRes.IsSuccessStatusCode)
                    logger.LogWarning("Calendar mirror: adding assignee to {CalendarProjectId} returned {Status}.", created.Id, (int)assigneeRes.StatusCode);
            }

            return (ProjectMirrorStatus.Mirrored, created.Id);
        }
        catch (Exception ex)
        {
            // Deliberately broad: mirroring is best-effort and must never fail project creation.
            logger.LogWarning(ex, "Calendar mirror failed for project {ProjectId}; recorded as Failed.", project.Id);
            return (ProjectMirrorStatus.Failed, null);
        }
    }

    private sealed record CalendarProjectRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("startDate")] string StartDate,
        [property: JsonPropertyName("endDate")] string EndDate);

    private sealed record CalendarProjectResponse(
        [property: JsonPropertyName("id")] string? Id);
}

public static class CalendarMirrorExtensions
{
    public static IServiceCollection AddCalendarMirror(this IServiceCollection services)
    {
        // Short timeout: mirroring runs inline in the create request, so a slow/unreachable Calendar
        // must fail fast rather than hold the user's request open.
        services.AddHttpClient<ICalendarMirror, CalendarMirror>(c => c.Timeout = TimeSpan.FromSeconds(10));
        return services;
    }
}
