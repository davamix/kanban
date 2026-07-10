using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace KanbanApi.Services;

/// <summary>
/// Obtains an access token for another app's API <em>on behalf of a user</em> via Logto's
/// OAuth2 token exchange (RFC 8693). It first mints an impersonation <c>subjectToken</c> for the
/// user (Management API), then exchanges it — authenticating as the confidential "exchange" client —
/// for a token whose <c>sub</c> is the user and whose audience is the target <paramref name="resource"/>.
/// This is how Kanban calls Calendar as the user (so Calendar sets that user as owner), never as
/// Kanban itself. See docs/ecosystem-integration.md §6.
/// </summary>
public interface ILogtoTokenExchange
{
    /// <summary>
    /// Returns an on-behalf-of access token for <paramref name="resource"/>, or null when the feature
    /// is unconfigured (no exchange client) or any leg fails — the caller degrades gracefully and
    /// never surfaces this as a request error.
    /// </summary>
    Task<string?> GetOnBehalfOfTokenAsync(string userId, string resource, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ILogtoTokenExchange"/> over the Logto OIDC token endpoint. The exchange client's
/// credentials (<c>Logto:Exchange:ClientId/Secret</c>) authenticate the exchange via HTTP Basic;
/// when they are unset the feature is off and this returns null. Fails soft (logs + null) on any
/// non-2xx / transport error, mirroring <see cref="LogtoManagementClient"/>'s contract.
/// </summary>
public sealed class LogtoTokenExchange(
    HttpClient http, IConfiguration config, ILogtoManagementClient management,
    ILogger<LogtoTokenExchange> logger) : ILogtoTokenExchange
{
    private const string TokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    public async Task<string?> GetOnBehalfOfTokenAsync(string userId, string resource, CancellationToken ct = default)
    {
        var issuer = config["Logto:Issuer"];
        var clientId = config["Logto:Exchange:ClientId"];
        var clientSecret = config["Logto:Exchange:ClientSecret"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(resource)
            || string.IsNullOrWhiteSpace(userId))
            return null;

        // Leg 1: mint the impersonation subject token (Logto cannot exchange a live session token).
        var subjectToken = await management.MintSubjectTokenAsync(userId, ct);
        if (subjectToken is null)
            return null;

        // Leg 2: exchange it for a token scoped to `resource`, authenticating as the exchange client.
        var tokenEndpoint = new Uri(new Uri(issuer), "token");
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = TokenExchangeGrant,
                ["subject_token"] = subjectToken,
                ["subject_token_type"] = AccessTokenType,
                ["resource"] = resource,
            }),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        try
        {
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Logto token exchange returned {Status} for resource {Resource}; on-behalf-of call skipped.", (int)res.StatusCode, resource);
                return null;
            }

            var payload = await res.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
            return string.IsNullOrWhiteSpace(payload?.AccessToken) ? null : payload.AccessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Logto token endpoint unreachable during exchange; on-behalf-of call skipped. Check Logto:Issuer.");
            return null;
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken);
}

public static class LogtoTokenExchangeExtensions
{
    public static IServiceCollection AddLogtoTokenExchange(this IServiceCollection services)
    {
        services.AddHttpClient<ILogtoTokenExchange, LogtoTokenExchange>();
        return services;
    }
}
