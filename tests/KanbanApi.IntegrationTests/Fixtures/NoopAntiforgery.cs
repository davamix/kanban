using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace KanbanApi.IntegrationTests.Fixtures;

/// <summary>
/// No-op antiforgery for tests — the in-memory test client doesn't carry the token cookie/header.
/// CSRF behaviour itself is exercised separately; endpoint tests don't need it.
/// </summary>
public sealed class NoopAntiforgery : IAntiforgery
{
    private static readonly AntiforgeryTokenSet Tokens = new("request", "cookie", "__RequestVerificationToken", "X-CSRF-TOKEN");

    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => Tokens;
    public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => Tokens;
    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);
    public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
    public void SetCookieTokenAndHeader(HttpContext httpContext) { }
}
