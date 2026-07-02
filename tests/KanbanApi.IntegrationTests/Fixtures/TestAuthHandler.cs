using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KanbanApi.IntegrationTests.Fixtures;

/// <summary>
/// Test authentication: a request authenticates as the user named in the <c>X-Test-User</c>
/// header (its value becomes the <c>sub</c>). No header → unauthenticated (401). This lets a test
/// act as different users, which is how the cross-user forgery tests are written.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var sub) || string.IsNullOrWhiteSpace(sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new[]
        {
            new Claim("sub", sub.ToString()),
            new Claim("name", sub.ToString()),
        };
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
