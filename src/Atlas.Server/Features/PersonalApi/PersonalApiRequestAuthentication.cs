using Microsoft.AspNetCore.Authentication;
using Atlas.Server.Features.Auth.Builtin;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>
/// Authenticates the bearer credential of a personal API channel path (<see cref="ApiChannels.ForPath"/>: /api/v1, and /mcp once the MCP
/// module names it) right after UseAuthentication, so the rate limiter that follows sees the token: personal-api partitions by the token,
/// and a request the authorization then refuses (403 scope_missing, audited since T3) has already spent the token's budget. Authorization
/// reuses the same result (an authentication handler runs once per request). A failed credential spends the client address's
/// <see cref="PersonalApiCredentialThrottle"/>; past it a credential that is not found answers 429, while a valid one from the same
/// (shared office or NAT) address still passes (SEC-A-4).
/// </summary>
public static class PersonalApiRequestAuthentication
{
    public static void UsePersonalApiAuthentication(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (ApiChannels.ForPath(context.Request.Path) is null || context.Request.Headers.Authorization.Count == 0)
            {
                await next();
                return;
            }
            var throttle = context.RequestServices.GetRequiredService<PersonalApiCredentialThrottle>();
            // The verified client address (UseAtlasForwardedHeaders), as the IP-partitioned rate-limit policies use it.
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var open = throttle.IsOpen(client, out var retryAfter);
            var result = await context.AuthenticateAsync(PersonalApiAuthentication.SchemeName);
            if (result.Succeeded)
            {
                context.User = result.Principal;
            }
            else if (result.Failure is not null)
            {
                throttle.RecordFailure(client);
                if (!open)
                {
                    context.Response.Headers.RetryAfter = BuiltinLoginThrottle.RetryAfterHeader(retryAfter);
                    await ProblemResponse.WriteAsync(context, 429, "Příliš mnoho neplatných pokusů o přihlášení tokenem. Zkuste to za chvíli.", "rate_limited");
                    return;
                }
            }
            await next();
        });
    }
}
