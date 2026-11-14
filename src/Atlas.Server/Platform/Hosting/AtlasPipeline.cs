using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Platform.Hosting;

/// <summary>
/// The request pipeline, in the order the security contract needs: response headers and error handling
/// wrap everything, rate limits apply before authorization so refused requests are bounded as well, CSRF runs after
/// authentication so an anonymous mutation answers 401 rather than 400, and static files come last before routing.
/// Only fingerprinted assets, public and immutable, are answered earlier, before authentication.
/// </summary>
public static class AtlasPipeline
{
    public static void UseAtlasPipeline(this WebApplication app)
    {
        app.UseAtlasServerTiming();
        app.UseAtlasForwardedHeaders();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            // Endpoints under /api rely on this and no longer set no-store themselves; do not narrow the condition.
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/signin-oidc"))
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            await RequestErrorHandling.InvokeAsync(context, next, app.Logger);
        });
        app.UseAtlasPublicSharingHeaders();
        app.UseAtlasContentSecurityPolicy();
        app.UseAtlasApiCompression();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }
        app.Use(async (context, next) =>
        {
            if ((context.Request.Path.StartsWithSegments("/hubs/markdown") || context.Request.Path.StartsWithSegments("/hubs/live"))
                && !CollaborationOrigin.IsAllowed(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
        app.Use(async (context, next) =>
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
            if (limit is not null && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
            {
                body.MaxRequestBodySize = limit;
            }
            await next();
        });
        var files = app.Services.GetRequiredService<PhysicalFileProvider>();
        // Fingerprinted assets need no credential: they leave here, before the session cookie costs its database lookup.
        app.UseAtlasFingerprintAssets(files);
        // /mcp: origin guard and module gate before any credential is read (MCP plan C1).
        app.UseMcpGate();
        app.UseAuthentication();
        // The limiter runs before authorization, so a refused request (401, 403 scope_missing, which is audited) spends its budget too.
        // A personal API token is authenticated just before it (cookies already are), so personal-api partitions by the token.
        app.UsePersonalApiAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/v1")
                && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            }
            await next();
        });
        // Source order moves this ahead of the endpoint registrations; the effective order does not change,
        // because Map* adds endpoints to a data source rather than middleware to the pipeline.
        app.UseAtlasStaticFiles(files);
    }
}
