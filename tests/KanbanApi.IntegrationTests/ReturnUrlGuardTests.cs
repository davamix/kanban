using FluentAssertions;
using KanbanApi.Endpoints;
using Xunit;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// The post-login <c>returnUrl</c> open-redirect guard (ASVS V3.7.2). Only a path rooted at a single
/// <c>/</c> is honoured; protocol-relative and absolute URLs fall back to the safe default so a
/// crafted <c>/login?returnUrl=…</c> can't send a freshly-authenticated user to an external origin.
/// </summary>
public sealed class ReturnUrlGuardTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/board.html?project=abc123", true)]
    [InlineData("/index.html", true)]
    [InlineData("//evil.com", false)]          // protocol-relative → external origin
    [InlineData("/\\evil.com", false)]         // backslash variant browsers treat as //
    [InlineData("https://evil.com", false)]    // absolute
    [InlineData("http:evil.com", false)]
    [InlineData("evil.com", false)]            // not rooted
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalReturnUrl_AcceptsOnlyLocalPaths(string? url, bool expected)
        => AuthEndpoints.IsLocalReturnUrl(url).Should().Be(expected);
}
