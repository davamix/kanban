using Microsoft.AspNetCore.Antiforgery;

namespace KanbanApi.Auth;

/// <summary>
/// CSRF protection for the cookie/BFF path. Issues a JS-readable token on navigations and
/// validates unsafe, cookie-authenticated requests. JWT-bearer (machine) callers carry no
/// ambient cookie credential, so CSRF does not apply to them and they are skipped.
/// </summary>
public static class AntiforgeryExtensions
{
    public static void UseSpaAntiforgery(this WebApplication app)
    {
        var antiforgery = app.Services.GetRequiredService<IAntiforgery>();

        app.Use(async (ctx, next) =>
        {
            // On HTML navigations, hand the SPA a readable token cookie to echo as X-CSRF-TOKEN.
            if (HttpMethods.IsGet(ctx.Request.Method)
                && ctx.Request.Headers.Accept.Any(a => a is not null && a.Contains("text/html")))
            {
                var tokens = antiforgery.GetAndStoreTokens(ctx);
                ctx.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
                {
                    HttpOnly = false,           // the SPA must read it
                    Secure = ctx.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                });
            }

            // Validate unsafe, cookie-authenticated mutations (logout + /api writes).
            if (IsUnsafe(ctx.Request.Method) && !HasBearer(ctx.Request))
            {
                try
                {
                    await antiforgery.ValidateRequestAsync(ctx);
                }
                catch (AntiforgeryValidationException)
                {
                    // RFC 9457 problem details — the consistent error shape used across the API.
                    await Results.Problem(
                        title: "Invalid antiforgery token.",
                        statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(ctx);
                    return;
                }
            }

            await next();
        });
    }

    private static bool IsUnsafe(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsDelete(method) || HttpMethods.IsPatch(method);

    private static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.Any(h =>
            h is not null && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));
}
