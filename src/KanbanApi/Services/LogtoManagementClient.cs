using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace KanbanApi.Services;

/// <summary>A user from the Logto directory, for the assignee picker.</summary>
public record DirectoryUser(string Id, string? Name, string? Email);

/// <summary>Reads the Logto user directory via the Management API (assignee picker).</summary>
public interface ILogtoManagementClient
{
    Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(string? search, CancellationToken ct = default);
}

/// <summary>
/// Management API client using an M2M client-credentials token. The token request asks for
/// <c>scope=all</c> against the built-in Management API resource — without it every call returns
/// 403 (a documented Logto gotcha; see docs/auth.md). Returns an empty list when not configured,
/// so the app runs before the M2M app is provisioned.
/// </summary>
public sealed class LogtoManagementClient(HttpClient http, IConfiguration config) : ILogtoManagementClient
{
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public async Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(string? search, CancellationToken ct = default)
    {
        var endpoint = config["Logto:Management:Endpoint"];
        var clientId = config["Logto:Management:ClientId"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(clientId))
            return [];

        var token = await GetTokenAsync(ct);
        if (token is null)
            return [];

        var url = $"{endpoint.TrimEnd('/')}/api/users";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"?q={Uri.EscapeDataString(search.Trim())}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return [];

        var users = await res.Content.ReadFromJsonAsync<List<LogtoUser>>(cancellationToken: ct) ?? [];
        return users.Select(u => new DirectoryUser(u.Id, u.Name ?? u.Username, u.PrimaryEmail)).ToList();
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _token;

        var issuer = config["Logto:Issuer"];
        var clientId = config["Logto:Management:ClientId"];
        var clientSecret = config["Logto:Management:ClientSecret"];
        var resource = config["Logto:Management:Resource"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(resource))
            return null;

        var tokenEndpoint = new Uri(new Uri(issuer), "token");
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["resource"] = resource,
                ["scope"] = "all",   // required, else Management API returns 403
            }),
        };
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return null;

        var payload = await res.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        if (payload?.AccessToken is null)
            return null;

        _token = payload.AccessToken;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, payload.ExpiresIn - 30));
        return _token;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record LogtoUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("primaryEmail")] string? PrimaryEmail);
}

public static class LogtoManagementClientExtensions
{
    public static IServiceCollection AddLogtoManagementClient(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<ILogtoManagementClient, LogtoManagementClient>();
        return services;
    }
}
