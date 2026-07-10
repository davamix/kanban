using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KanbanApi.Models;

namespace KanbanApi.Services;

/// <summary>
/// Mirrors a Kanban project's lifecycle (create / edit / delete) into the Calendar app, on-behalf-of
/// its owner. Every method is a best-effort side effect: it MUST NOT throw and MUST NOT block the
/// Kanban operation — failures degrade to <see cref="ProjectMirrorStatus.Failed"/> (or
/// <see cref="ProjectMirrorStatus.Skipped"/> when the feature is unconfigured). Tasks are never
/// mirrored. See docs/ecosystem-integration.md §6 and ADR 0009/0010.
/// </summary>
public interface ICalendarMirror
{
    Task<(ProjectMirrorStatus Status, string? CalendarProjectId)> MirrorProjectAsync(
        Project project, IReadOnlyList<string> assigneeIds, CancellationToken ct = default);

    /// <summary>
    /// Propagates a project edit to its existing Calendar counterpart, on-behalf-of the owner: replaces
    /// the scalar fields (name/description/dates) and reconciles the assignee delta (adds
    /// <paramref name="addedAssigneeIds"/>, removes <paramref name="removedAssigneeIds"/>). Only called
    /// when the project already has a <c>CalendarProjectId</c>. Returns <see cref="ProjectMirrorStatus.Mirrored"/>
    /// on success and <see cref="ProjectMirrorStatus.Failed"/> when the edit can't be propagated (hard
    /// failure, or the exchange/config being unavailable) — a counterpart exists, so an unpropagated edit
    /// is drift, never <see cref="ProjectMirrorStatus.Skipped"/>. Never throws.
    /// </summary>
    Task<ProjectMirrorStatus> UpdateProjectAsync(
        Project project, IReadOnlyList<string> addedAssigneeIds, IReadOnlyList<string> removedAssigneeIds,
        CancellationToken ct = default);

    /// <summary>
    /// Propagates a project deletion to its Calendar counterpart, on-behalf-of the owner. Only called
    /// when the project has a <c>CalendarProjectId</c>. Best-effort: returns <c>true</c> on success (or a
    /// <c>404</c> already-gone), <c>false</c> otherwise. Never throws — a failure just leaves a Calendar
    /// orphan; the Kanban delete always proceeds.
    /// </summary>
    Task<bool> DeleteProjectAsync(Project project, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ICalendarMirror"/> against Calendar's public REST API. Acquires an on-behalf-of token
/// (RFC 8693) for Calendar's audience via <see cref="ILogtoTokenExchange"/> (shared <c>TryPrepareAsync</c>),
/// then creates / updates / deletes the Calendar project and reconciles its assignees. Degrades to
/// <see cref="ProjectMirrorStatus.Skipped"/>/no-op when the Calendar base URL or the exchange client is
/// unset (feature off), so standalone dev is unaffected. Everything is wrapped so no error escapes.
/// </summary>
public sealed class CalendarMirror(
    HttpClient http, IConfiguration config, ILogtoTokenExchange tokenExchange,
    ILogger<CalendarMirror> logger) : ICalendarMirror
{
    public async Task<(ProjectMirrorStatus Status, string? CalendarProjectId)> MirrorProjectAsync(
        Project project, IReadOnlyList<string> assigneeIds, CancellationToken ct = default)
    {
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
            var prep = await TryPrepareAsync(project.OwnerId, linked);
            if (prep is null)
                return (ProjectMirrorStatus.Skipped, null);   // feature off / exchange unavailable
            var (root, token) = prep.Value;

            var createBody = new CalendarProjectRequest(
                project.Name,
                project.Description,
                project.StartDate.ToString("yyyy-MM-dd"),
                project.EndDate.ToString("yyyy-MM-dd"));

            using var createReq = Authorized(HttpMethod.Post, $"{root}/api/projects", token, JsonContent.Create(createBody));
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
                await AddAssigneeAsync(root, created.Id, userId, token, linked);

            return (ProjectMirrorStatus.Mirrored, created.Id);
        }
        catch (Exception ex)
        {
            // Deliberately broad: mirroring is best-effort and must never fail project creation.
            logger.LogWarning(ex, "Calendar mirror failed for project {ProjectId}; recorded as Failed.", project.Id);
            return (ProjectMirrorStatus.Failed, null);
        }
    }

    public async Task<ProjectMirrorStatus> UpdateProjectAsync(
        Project project, IReadOnlyList<string> addedAssigneeIds, IReadOnlyList<string> removedAssigneeIds,
        CancellationToken ct = default)
    {
        // Defensive: callers only invoke this for already-mirrored projects, but never build a request
        // against a null id — it would collapse the URL to a collection-level PUT (all projects).
        if (string.IsNullOrWhiteSpace(project.CalendarProjectId))
            return ProjectMirrorStatus.Skipped;

        // Same overall deadline posture as create: bound the whole exchange → PUT → assignee reconcile.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var linked = timeout.Token;

        try
        {
            var prep = await TryPrepareAsync(project.OwnerId, linked);
            if (prep is null)
                // The project already has a Calendar counterpart (the caller only invokes this for
                // mirrored projects), so an inability to reach it — a transient exchange/Logto outage,
                // or config toggled off — means this edit did not land. Surface the drift as Failed;
                // never silently downgrade Mirrored → Skipped (which means "no counterpart"). ADR 0010 §4.
                return ProjectMirrorStatus.Failed;
            var (root, token) = prep.Value;

            var updateBody = new CalendarProjectRequest(
                project.Name,
                project.Description,
                project.StartDate.ToString("yyyy-MM-dd"),
                project.EndDate.ToString("yyyy-MM-dd"));

            using var putReq = Authorized(HttpMethod.Put, $"{root}/api/projects/{project.CalendarProjectId}", token, JsonContent.Create(updateBody));
            using var putRes = await http.SendAsync(putReq, linked);
            if (!putRes.IsSuccessStatusCode)
            {
                logger.LogWarning("Calendar mirror: update returned {Status} for project {ProjectId} (calendar {CalendarProjectId}).", (int)putRes.StatusCode, project.Id, project.CalendarProjectId);
                return ProjectMirrorStatus.Failed;
            }

            // Reconcile assignees (owner is auto-managed by Calendar and never in the delta). An assignee
            // failure only logs — the scalar update already landed, so we still report Mirrored.
            foreach (var userId in removedAssigneeIds)
            {
                using var delReq = Authorized(HttpMethod.Delete, $"{root}/api/projects/{project.CalendarProjectId}/assignees/{userId}", token);
                using var delRes = await http.SendAsync(delReq, linked);
                if (!delRes.IsSuccessStatusCode)
                    logger.LogWarning("Calendar mirror: removing assignee from {CalendarProjectId} returned {Status}.", project.CalendarProjectId, (int)delRes.StatusCode);
            }
            foreach (var userId in addedAssigneeIds)
                await AddAssigneeAsync(root, project.CalendarProjectId!, userId, token, linked);

            return ProjectMirrorStatus.Mirrored;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Calendar mirror update failed for project {ProjectId}; recorded as Failed.", project.Id);
            return ProjectMirrorStatus.Failed;
        }
    }

    public async Task<bool> DeleteProjectAsync(Project project, CancellationToken ct = default)
    {
        // Defensive: never issue a DELETE against a null id — it would collapse the URL to a
        // collection-level DELETE (all projects). Callers only invoke this for mirrored projects.
        if (string.IsNullOrWhiteSpace(project.CalendarProjectId))
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var linked = timeout.Token;

        try
        {
            var prep = await TryPrepareAsync(project.OwnerId, linked);
            if (prep is null)
                return false;   // feature off / exchange unavailable — nothing propagated
            var (root, token) = prep.Value;

            using var delReq = Authorized(HttpMethod.Delete, $"{root}/api/projects/{project.CalendarProjectId}", token);
            using var delRes = await http.SendAsync(delReq, linked);
            // A 404 means the counterpart is already gone — treat it as a successful delete.
            if (delRes.IsSuccessStatusCode || delRes.StatusCode == System.Net.HttpStatusCode.NotFound)
                return true;

            logger.LogWarning("Calendar mirror: delete returned {Status} for project {ProjectId} (calendar {CalendarProjectId}).", (int)delRes.StatusCode, project.Id, project.CalendarProjectId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Calendar mirror delete failed for project {ProjectId} (calendar {CalendarProjectId}).", project.Id, project.CalendarProjectId);
            return false;
        }
    }

    // Resolves Calendar's base URL and an on-behalf-of bearer for the owner, under the caller's deadline.
    // Null ⇒ the feature is off (base URL / audience unset) or the exchange is unavailable — the caller
    // degrades to Skipped. Owner comes from the token's `sub`, so we exchange for the owner's sub.
    private async Task<(string Root, string Token)?> TryPrepareAsync(string ownerId, CancellationToken linked)
    {
        var baseUrl = config["Calendar:Api:BaseUrl"];
        var resource = config["Calendar:Api:Resource"];
        // Both the endpoint and Calendar's audience are configuration (no audience is baked into code —
        // it differs per ecosystem/deployment). Either unset ⇒ mirroring is off for this deployment.
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(resource))
            return null;
        var token = await tokenExchange.GetOnBehalfOfTokenAsync(ownerId, resource, linked);
        return token is null ? null : (baseUrl.TrimEnd('/'), token);
    }

    private async Task AddAssigneeAsync(string root, string calendarProjectId, string userId, string token, CancellationToken linked)
    {
        using var req = Authorized(HttpMethod.Post, $"{root}/api/projects/{calendarProjectId}/assignees", token, JsonContent.Create(new { userId }));
        using var res = await http.SendAsync(req, linked);
        if (!res.IsSuccessStatusCode)
            logger.LogWarning("Calendar mirror: adding assignee to {CalendarProjectId} returned {Status}.", calendarProjectId, (int)res.StatusCode);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token, HttpContent? content = null) =>
        new(method, url)
        {
            Content = content,
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };

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
        // Short timeout: mirroring runs inline in the create/edit/delete request, so a slow/unreachable
        // Calendar must fail fast rather than hold the user's request open.
        services.AddHttpClient<ICalendarMirror, CalendarMirror>(c => c.Timeout = TimeSpan.FromSeconds(10));
        return services;
    }
}
