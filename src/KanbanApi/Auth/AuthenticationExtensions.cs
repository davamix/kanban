using System.Threading.RateLimiting;
using KanbanApi.Data;
using KanbanApi.Models;
using KanbanApi.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;

namespace KanbanApi.Auth;

/// <summary>
/// Wires Kanban's dual authentication (BFF cookie for the browser + JWT bearer for machine
/// callers), the shared authorization policy, antiforgery, and the API rate limiter.
/// See docs/auth.md and docs/security/asvs-l2/.
/// </summary>
public static class AuthenticationExtensions
{
    private static readonly TimeSpan AbsoluteSessionLifetime = TimeSpan.FromDays(7);

    private static async Task UpsertLocalUserAsync(Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext ctx)
    {
        var sub = ctx.Principal?.FindFirst("sub")?.Value;
        if (sub is null)
            return;

        var db = ctx.HttpContext.RequestServices.GetRequiredService<KanbanDbContext>();
        var email = ctx.Principal!.FindFirst("email")?.Value;
        var name = ctx.Principal.FindFirst("name")?.Value ?? ctx.Principal.FindFirst("username")?.Value;

        // Session claims can be thin (a username-only account may carry no name/email claim), which
        // would leave the mirror — and everything that reads it (header, assignee chips) — showing a
        // raw id. Backfill from the Logto directory so the mirror is complete. The directory client
        // fails soft (empty on any error), so a fresh login never breaks if Logto is unavailable.
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            var directory = ctx.HttpContext.RequestServices.GetRequiredService<ILogtoManagementClient>();
            var dir = (await directory.GetUsersAsync(null)).FirstOrDefault(u => u.Id == sub);
            name = string.IsNullOrWhiteSpace(name) ? dir?.Name : name;
            email = string.IsNullOrWhiteSpace(email) ? dir?.Email : email;
        }

        var user = await db.Users.FindAsync(sub);
        if (user is null)
            db.Users.Add(new AppUser { Id = sub, Email = email, DisplayName = name });
        else
        {
            user.Email = email ?? user.Email;
            user.DisplayName = name ?? user.DisplayName;
        }
        await db.SaveChangesAsync();
    }

    public static void AddKanbanAuth(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var isDev = builder.Environment.IsDevelopment();

        var issuer = config["Logto:Issuer"]
            ?? throw new InvalidOperationException("Missing Logto:Issuer (LOGTO__ISSUER).");
        var audience = config["Logto:Audience"]
            ?? throw new InvalidOperationException("Missing Logto:Audience (LOGTO__AUDIENCE).");
        var clientId = config["Logto:Web:ClientId"];
        var clientSecret = config["Logto:Web:ClientSecret"];
        // The BFF (browser sign-in) is only wired when a Logto web client is configured. Until then
        // the app still runs as a JWT resource server, so it boots cleanly before the one-time Logto
        // console setup instead of failing OIDC option validation on every request.
        var bffEnabled = !string.IsNullOrWhiteSpace(clientId);

        // Keep the raw 'sub' claim on every scheme — never remap it to ClaimTypes.NameIdentifier,
        // which would break owner/assignee identity resolution (ASVS V10.3.3).
        JsonWebTokenHandler.DefaultMapInboundClaims = false;

        var authBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = bffEnabled
                ? OpenIdConnectDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        });

        // BFF session cookie — tokens stay server-side (ASVS V10.1.1).
        authBuilder.AddCookie(options =>
            {
                options.Cookie.Name = "kanban.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Plain http on localhost in dev would drop a Secure cookie → login loop.
                options.Cookie.SecurePolicy = isDev
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);

                // API requests get a status code, not a 302 to the IdP.
                options.Events.OnRedirectToLogin = ctx =>
                {
                    if (ctx.Request.Path.StartsWithSegments("/api"))
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    else
                        ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    if (ctx.Request.Path.StartsWithSegments("/api"))
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    else
                        ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                };

                // Absolute session cap on top of the 12h sliding window (ASVS V7.3).
                options.Events.OnSigningIn = ctx =>
                {
                    ctx.Properties.SetString(
                        "absexp",
                        DateTimeOffset.UtcNow.Add(AbsoluteSessionLifetime).ToUnixTimeSeconds().ToString());
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = async ctx =>
                {
                    var raw = ctx.Properties.GetString("absexp");
                    if (long.TryParse(raw, out var sec)
                        && DateTimeOffset.UtcNow > DateTimeOffset.FromUnixTimeSeconds(sec))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });

        // OIDC code flow to Logto's hosted sign-in/sign-up page — registered only when configured.
        if (bffEnabled)
        {
            authBuilder.AddOpenIdConnect(options =>
            {
                options.Authority = issuer;
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.ResponseType = "code";
                // Query response mode (default is form_post): the callback is then a top-level GET,
                // so the SameSite=Lax correlation/nonce cookies are sent on the cross-site redirect
                // back from Logto (a cross-site POST would drop them → "Correlation failed").
                options.ResponseMode = "query";
                options.UsePkce = true;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !isDev;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.Scope.Add("offline_access");
                options.TokenValidationParameters.NameClaimType = "name";
                // The login round-trip is cross-site when the app (localhost) and Logto
                // (auth.kanban.localhost) are different hosts behind the proxy. The callback is a
                // top-level GET, so Lax correlation/nonce cookies survive it — and stay sendable
                // over plain http in dev (SameSite=None would require Secure and be dropped).
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.NonceCookie.SecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                // RFC 8707 resource indicator → Logto mints a JWT access token for the Kanban API.
                options.Events.OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.SetParameter("resource", audience);
                    return Task.CompletedTask;
                };
                // Mirror the user locally on sign-in so owner/assignee references are FK-backed
                // and the UI can show names without re-querying Logto.
                options.Events.OnTokenValidated = UpsertLocalUserAsync;
            });
        }

        // Resource server for machine / inter-app callers.
        authBuilder.AddJwtBearer(options =>
        {
            options.Authority = issuer;
            options.Audience = audience;
            options.RequireHttpsMetadata = !isDev;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidateLifetime = true;
            options.TokenValidationParameters.NameClaimType = "sub";
        });

        builder.Services.AddAuthorization(options =>
        {
            // /api/* accepts EITHER the BFF cookie OR a valid JWT bearer.
            options.DefaultPolicy = new AuthorizationPolicyBuilder(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build();
        });

        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();

        // Per-subject (falling back to per-IP) rate limit on the API surface (ASVS V2.4.1).
        var perMinute = config.GetValue("RateLimiting:PerMinute", 100);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                if (!ctx.Request.Path.StartsWithSegments("/api"))
                    return RateLimitPartition.GetNoLimiter("unlimited");

                var key = ctx.User.FindFirst("sub")?.Value
                          ?? ctx.Connection.RemoteIpAddress?.ToString()
                          ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute,
                    Window = TimeSpan.FromMinutes(1),
                });
            });
        });
    }
}
