using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Platform.Hosting;

namespace Atlas.Server.Features.Auth;

public static class AuthCookieModule
{
    /// <summary>The browser session: data protection keyring plus the cookie scheme. Web hosts only.</summary>
    public static IServiceCollection AddAuthCookies(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddDataProtection()
            .SetApplicationName("ScaleAtlas")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "keys")));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "ScaleAtlas.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            // Any request in the second week renews the session, so only two weeks without Atlas sign one out. The collaborative
            // editor talks over a WebSocket that never renews the cookie; an eight-hour window ended sessions of people at work.
            // Sign-out, a password change and deactivation still end a session at once (revocation, security stamp).
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = async context =>
            {
                if (BrowserSessionRevocations.SessionId(context.Principal) is not { } sessionId)
                {
                    context.RejectPrincipal();
                    return;
                }
                // The SPA shell and the unversioned files of the web root do not read the user. No other request skips the lookup;
                // fingerprinted assets are answered before authentication (AtlasPipeline) and never get here.
                if (AtlasHttpHosting.IsAnsweredByStaticFiles(context.HttpContext))
                {
                    return;
                }
                var session = context.HttpContext.RequestServices.GetRequiredService<NHibernate.ISession>();
                if (await BrowserSessionRevocations.IsRevokedAsync(session, sessionId, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                }
            };
            // An API caller gets the same problem+json body the error middleware writes for an
            // ApiException; only a browser navigation is left with the bare status code.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = 401;
                return IsApi(context.Request)
                    ? ProblemResponse.WriteAsync(context.HttpContext, 401, "Přihlaste se.")
                    : Task.CompletedTask;
            };
            // Unreachable while every group is a bare RequireAuthorization(), which only challenges.
            // The first policy-bearing group (wave 4 administration and personal API) makes it live.
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = 403;
                return IsApi(context.Request)
                    ? ProblemResponse.WriteAsync(context.HttpContext, 403, "Přístup byl odepřen.")
                    : Task.CompletedTask;
            };
        });
        return services;
    }

    private static bool IsApi(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api");
    }
}
