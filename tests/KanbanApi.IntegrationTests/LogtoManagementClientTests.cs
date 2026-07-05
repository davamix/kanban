using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KanbanApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// The assignee directory must fail *soft*: a misconfigured or unreachable Logto Management API
/// yields an empty directory (owner-only assignment), never an unhandled 500 that would take down
/// the assignee picker and project creation. Regression for the "Connection refused" incident.
/// </summary>
public sealed class LogtoManagementClientTests
{
    // Routes token requests to a canned success (or throws) and lets the caller decide the users call.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logto:Issuer"] = "https://logto.test/oidc/",
            ["Logto:Management:Endpoint"] = "https://logto.test",
            ["Logto:Management:ClientId"] = "m2m",
            ["Logto:Management:ClientSecret"] = "secret",
            ["Logto:Management:Resource"] = "https://default.logto.app/api",
        }).Build();

    private static LogtoManagementClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new StubHandler(respond)), Config, NullLogger<LogtoManagementClient>.Instance);

    private static HttpResponseMessage TokenOk() =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "t", expires_in = 3600 }) };

    [Fact]
    public async Task GetUsers_WhenDirectoryConnectionRefused_ReturnsEmpty()
    {
        // Token succeeds; the /api/users call throws a transport error (connection refused).
        var client = Client(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/oidc/token")
                ? TokenOk()
                : throw new HttpRequestException("Connection refused (localhost:3001)"));

        var users = await client.GetUsersAsync(null);

        users.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsers_WhenTokenEndpointUnreachable_ReturnsEmpty()
    {
        var client = Client(_ => throw new HttpRequestException("Connection refused"));

        var users = await client.GetUsersAsync(null);

        users.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsers_WhenDirectoryReturns403_ReturnsEmpty()
    {
        var client = Client(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/oidc/token")
                ? TokenOk()
                : new HttpResponseMessage(HttpStatusCode.Forbidden));

        var users = await client.GetUsersAsync(null);

        users.Should().BeEmpty();
    }
}
