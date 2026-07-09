using KanbanApi.Data;
using KanbanApi.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Endpoints;

/// <summary>
/// The BFF surface: sign-in (challenge to Logto's hosted page), sign-out (RP-initiated logout),
/// and the current-user endpoint the SPA reads on load. See docs/auth.md.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Browser sign-in: OIDC challenge → Logto hosted sign-in/sign-up page.
        // returnUrl must be a local path (open-redirect guard, ASVS V3.7.2).
        app.MapGet("/login", (IConfiguration config, string? returnUrl) =>
        {
            if (string.IsNullOrWhiteSpace(config["Logto:Web:ClientId"]))
                return Results.Problem(
                    "Browser sign-in is not configured. Set LOGTO__WEB__CLIENTID after the Logto "
                    + "console setup (see docs/auth.md).",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var target = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = target },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous();

        // Logout: antiforgery-protected POST (validated by UseSpaAntiforgery) → local sign-out +
        // Logto end_session (when the BFF is configured), returning to the post-logout redirect.
        app.MapPost("/logout", (IConfiguration config) =>
        {
            string[] schemes = string.IsNullOrWhiteSpace(config["Logto:Web:ClientId"])
                ? [CookieAuthenticationDefaults.AuthenticationScheme]
                : [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme];
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, schemes);
        });

        // Who am I — drives the SPA header + the auto-assigned creator. Prefer the local user mirror
        // (kept in sync from Logto at login), which carries a resolved name/email even when the
        // session claims are thin (e.g. a username-only account with no `name` claim); fall back to
        // the claims. Read-only: the mirror is populated/enriched at login, not here.
        app.MapGet("/api/me", async (ICurrentUser me, KanbanDbContext db, CancellationToken ct) =>
        {
            var user = me.Id is { } id
                ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct)
                : null;
            return Results.Ok(new
            {
                id = me.Id,
                email = user?.Email ?? me.Email,
                displayName = user?.DisplayName ?? me.DisplayName,
            });
        }).RequireAuthorization();
    }

    /// <summary>
    /// Open-redirect guard for the post-login <c>returnUrl</c> (ASVS V3.7.2). A bare
    /// <see cref="Uri.IsWellFormedUriString(string, UriKind)"/> with <see cref="UriKind.Relative"/> is
    /// insufficient: it accepts protocol-relative <c>//host</c> and <c>/\host</c> references, which a
    /// browser resolves to an external origin, turning the post-login redirect into a phishing vector.
    /// Only a path rooted at a single <c>/</c> (and not <c>//</c> or <c>/\</c>) is accepted — the same
    /// rule as ASP.NET Core's <c>Url.IsLocalUrl</c>. Everything else falls back to <c>/</c>.
    /// </summary>
    internal static bool IsLocalReturnUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
            return false;
        return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
    }
}
