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

            var target = returnUrl is not null && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                ? returnUrl
                : "/";
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
}
