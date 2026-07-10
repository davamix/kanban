using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KanbanApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// On-behalf-of token exchange (RFC 8693): mint an impersonation subject token, then exchange it as
/// the confidential exchange client for a token scoped to the target resource. Off (null) when the
/// exchange client is unconfigured, and fails soft (null) on any error — never surfaced as a request
/// failure. The user identity flows from the caller's sub, never client input. See ADR 0009.
/// </summary>
public sealed class LogtoTokenExchangeTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    // Stands in for the Management API leg: returns a subject token for the requested user (or null).
    private sealed class FakeManagement(string? subjectToken) : ILogtoManagementClient
    {
        public string? RequestedUserId { get; private set; }
        public Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(string? search, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DirectoryUser>>([]);
        public Task<string?> MintSubjectTokenAsync(string userId, CancellationToken ct = default)
        {
            RequestedUserId = userId;
            return Task.FromResult(subjectToken);
        }
    }

    private static IConfiguration Config(bool configured = true) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logto:Issuer"] = "https://logto.test/oidc/",
            ["Logto:Exchange:ClientId"] = configured ? "exchange" : null,
            ["Logto:Exchange:ClientSecret"] = configured ? "secret" : null,
        }).Build();

    [Fact]
    public async Task Exchange_MintsSubjectToken_AndReturnsAccessToken()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "obo-token" }) });
        var management = new FakeManagement("subj-123");
        var sut = new LogtoTokenExchange(new HttpClient(handler), Config(), management, NullLogger<LogtoTokenExchange>.Instance);

        var token = await sut.GetOnBehalfOfTokenAsync("user-42", "https://calendar.api");

        token.Should().Be("obo-token");
        management.RequestedUserId.Should().Be("user-42");   // identity from the caller's sub
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/oidc/token");
        handler.LastBody.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange");
        handler.LastBody.Should().Contain("subject_token=subj-123");
        handler.LastBody.Should().Contain("resource=https%3A%2F%2Fcalendar.api");
        // Client authenticates via HTTP Basic (the exchange client credentials).
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Basic");
    }

    [Fact]
    public async Task Exchange_WhenNotConfigured_ReturnsNull_WithoutMinting()
    {
        var management = new FakeManagement("subj-123");
        var sut = new LogtoTokenExchange(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Config(configured: false), management, NullLogger<LogtoTokenExchange>.Instance);

        var token = await sut.GetOnBehalfOfTokenAsync("user-42", "https://calendar.api");

        token.Should().BeNull();
        management.RequestedUserId.Should().BeNull();   // short-circuits before minting
    }

    [Fact]
    public async Task Exchange_WhenSubjectTokenUnavailable_ReturnsNull()
    {
        var management = new FakeManagement(null);   // minting failed / feature off upstream
        var sut = new LogtoTokenExchange(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Config(), management, NullLogger<LogtoTokenExchange>.Instance);

        (await sut.GetOnBehalfOfTokenAsync("user-42", "https://calendar.api")).Should().BeNull();
    }

    [Fact]
    public async Task Exchange_WhenTokenEndpointErrors_ReturnsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var sut = new LogtoTokenExchange(new HttpClient(handler), Config(), new FakeManagement("subj"), NullLogger<LogtoTokenExchange>.Instance);

        (await sut.GetOnBehalfOfTokenAsync("user-42", "https://calendar.api")).Should().BeNull();
    }
}
