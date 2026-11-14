using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Developer;
using Atlas.Server.Features.Auth.OpenIdConnect.Commands;
using Atlas.Server.Features.Auth.OpenIdConnect.Queries;
using Atlas.Server.Features.Auth.Queries;
using Wolverine;

namespace Atlas.Server.Features.Auth.OpenIdConnect;

public static class OidcEndpoints
{
    private const string Scheme = OpenIddictClientAspNetCoreDefaults.AuthenticationScheme;

    public static IEndpointRouteBuilder MapOpenIdConnect(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/methods", (AtlasOidcOptions settings, HttpContext context) =>
        {
            var builtinEnabled = settings.BuiltinEnabled;
            var providers = settings.Providers.Select(p => new { id = p.Id, displayName = p.DisplayName, loginUrl = "/api/auth/oidc/" + p.Id + "/login" });
            // The property exists only where all of the developer sign-in's guards pass for this very request.
            return DeveloperSignIn.IsAllowed(context)
                ? Results.Ok(new { builtinEnabled, providers, developerSignIn = true })
                : Results.Ok(new { builtinEnabled, providers });
        });
        if (endpoints.ServiceProvider.GetRequiredService<AtlasOidcOptions>().Providers.Length == 0)
        {
            return endpoints;
        }
        endpoints.MapGet("/api/auth/oidc/{id}/login", async (string id, string? returnUrl, AtlasOidcOptions settings, IMessageBus bus, HttpContext context, CancellationToken ct) =>
        {
            var provider = FindProvider(settings, id);
            if (!(await bus.InvokeAsync<WorkspaceState>(new CheckAuthenticationReady(), ct)).Configured)
            {
                throw new ApiException(409, "Nejprve dokončete úvodní nastavení.");
            }
            var properties = ChallengeProperties(provider, returnUrl);
            await ChallengeAsync(context, properties);
        }).RequireRateLimiting("authentication");
        endpoints.MapGet("/api/auth/oidc/{id}/test-mapping", async (string id, AtlasOidcOptions settings, ICurrentActor currentActor, NHibernate.ISession session, HttpContext context, CancellationToken ct) =>
        {
            var actor = await currentActor.RequireAsync(ct);
            if (!await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct))
            {
                throw new ApiException(403, "Mapping může ověřit pouze správce wiki.");
            }
            var sessionId = context.User.FindFirstValue("atlas_session");
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ApiException(401, "Pro ověření mappingu se znovu přihlaste.");
            }
            var properties = ChallengeProperties(FindProvider(settings, id), "/");
            properties.Items["atlas:operation"] = "test-mapping";
            properties.Items["atlas:user"] = actor.Id.ToString();
            properties.Items["atlas:stamp"] = actor.SecurityStamp;
            properties.Items["atlas:session"] = sessionId;
            await ChallengeAsync(context, properties);
        }).RequireRateLimiting("authentication");
        endpoints.MapGet("/api/profile/identities", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<LinkedIdentitiesDto>(new ListIdentities(), ct)));
        endpoints.MapPost("/api/profile/identities/{id}/link", async (string id, LinkExternalIdentity request, AtlasOidcOptions settings, IMessageBus bus, HttpContext context, CancellationToken ct) =>
        {
            var provider = FindProvider(settings, id);
            // Freshness is a claim question, so the endpoint answers it and the handler only trusts the verdict.
            var recentProviderAuthentication = SignInPrincipal.HasRecentAuthentication(context.User, DateTimeOffset.UtcNow)
                && context.User.FindFirstValue("authentication_method")?.StartsWith("oidc:", StringComparison.Ordinal) == true;
            LinkChallenge challenge;
            try
            {
                challenge = await bus.InvokeAsync<LinkChallenge>(new AuthorizeIdentityLink(request.Password, recentProviderAuthentication), ct);
            }
            catch (ApiException refusal) when (refusal.Code == BuiltinLoginThrottle.RefusalCode)
            {
                context.Response.Headers.RetryAfter = (string)refusal.Extensions!["retryAfter"]!;
                throw;
            }
            var properties = ChallengeProperties(provider, request.ReturnUrl);
            properties.Items["atlas:operation"] = "link";
            properties.Items["atlas:user"] = challenge.UserId.ToString();
            properties.Items["atlas:stamp"] = challenge.SecurityStamp;
            await ChallengeAsync(context, properties);
            var location = context.Response.Headers.Location.ToString();
            if (context.Response.StatusCode != 302 || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            {
                throw new ApiException(503, "Poskytovatel přihlášení není dostupný.");
            }
            context.Response.Headers.Remove("Location");
            context.Response.StatusCode = 200;
            await context.Response.WriteAsJsonAsync(new { loginUrl = location }, ct);
        }).RequireRateLimiting("authentication");
        endpoints.MapMethods("/signin-oidc/{id}", ["GET", "POST"], async (string id, AtlasOidcOptions settings, IMessageBus bus, ICurrentActor currentActor, NHibernate.ISession session, ISignInService signIn, Atlas.Server.Features.Hub.HubSynchronizer hub, HttpContext context, CancellationToken ct) =>
        {
            // Which step refused the login, logged without the protocol's payload (tokens, remote texts) so a failed login is traceable.
            var step = "authenticate";
            IResult Failed(string reason)
            {
                context.RequestServices.GetRequiredService<ILogger<AtlasOidcOptions>>().LogWarning("OIDC sign-in failed at {Step}: {Reason}.", step, reason);
                return Results.Redirect("/?authError=external_login_failed");
            }
            try
            {
                var provider = FindProvider(settings, id);
                var result = await context.AuthenticateAsync(Scheme);
                if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true || result.Properties is null || result.Principal.GetClaim(OpenIddictConstants.Claims.Private.RegistrationId) != id)
                {
                    return Failed(!result.Succeeded ? "not authenticated" : result.Properties is null ? "no properties" : "registration mismatch");
                }
                var subject = result.Principal.GetClaim(OpenIddictConstants.Claims.Subject);
                if (string.IsNullOrEmpty(subject) || subject.Length > 1024)
                {
                    return Failed("no usable subject");
                }
                if (result.Properties.GetString("atlas:operation") == "reauth")
                {
                    return await CompleteReauthAsync(provider, result, subject, currentActor, bus, context, ct);
                }
                if (result.Properties.GetString("atlas:operation") == "test-mapping")
                {
                    var actor = await currentActor.RequireAsync(ct);
                    if (!await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct) || result.Properties.GetString("atlas:user") != actor.Id.ToString()
                        || result.Properties.GetString("atlas:stamp") != actor.SecurityStamp
                        || string.IsNullOrEmpty(result.Properties.GetString("atlas:session"))
                        || result.Properties.GetString("atlas:session") != context.User.FindFirstValue("atlas_session"))
                    {
                        return Results.Forbid();
                    }
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
                    var hubManaged = await session.CreateSQLQuery("SELECT EXISTS (SELECT 1 FROM \"HubConnectionRevision\" WHERE \"Issuer\"=:issuer AND \"ActiveTo\" IS NULL)")
                        .SetParameter("issuer", provider.Issuer).UniqueResultAsync<bool>(ct);
                    var message = hubManaged
                        ? "Oprávnění tohoto poskytovatele spravuje Hub. Mapování claimů nemění roli správce wiki."
                        : OidcAdminMapping.Matches(result.Principal, provider.AdminMappings)
                        ? "Pravidlo odpovídá: tento firemní účet při běžném přihlášení získá správce wiki."
                        : "Žádné pravidlo neodpovídá. Zkontrolujte název claimu, hodnotu skupiny nebo role a scopes poskytovatele.";
                    return Results.Content("<!doctype html><html lang=\"cs\"><meta charset=\"utf-8\"><title>Ověření mappingu</title><h1>Ověření mappingu</h1><p>"
                        + message + "</p><p>Zkouška nezměnila účet ani oprávnění. Tuto kartu můžete zavřít.</p></html>", "text/html; charset=utf-8");
                }
                UserAccount? link = null;
                if (result.Properties.GetString("atlas:operation") == "link")
                {
                    link = await currentActor.RequireAsync(ct);
                    if (result.Properties.GetString("atlas:user") != link.Id.ToString() || result.Properties.GetString("atlas:stamp") != link.SecurityStamp)
                    {
                        step = "link";
                        return Failed("another account or stamp");
                    }
                }
                step = "resolve";
                var user = await bus.InvokeAsync<UserAccount>(new ResolveExternalIdentity(provider.Issuer, provider.AllowSignup, subject,
                    FirstClaim(result.Principal, OpenIddictConstants.Claims.Name), link?.Id, link?.SecurityStamp,
                    OidcAdminMapping.Matches(result.Principal, provider.AdminMappings),
                    FirstClaim(result.Principal, OpenIddictConstants.Claims.EmailVerified) is "true" or "True" ? FirstClaim(result.Principal, OpenIddictConstants.Claims.Email) : null), ct);
                step = "hub";
                await hub.VerifyLoginAsync(user.Id, provider.Issuer, subject, ct);
                step = "sign-in";
                await signIn.SignInAsync(context, user, "oidc:" + provider.Id);
                return Results.Redirect(OidcReturnUrl.Safe(result.Properties.RedirectUri));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Protocol exceptions may contain tokens, remote payloads or secrets: only the type, and an ApiException's own status and text.
                // Past "authenticate" the exception is Atlas's own: its stack, and the text of the ones whose text Atlas writes (no claims or SQL).
                return Failed(ex is ApiException api ? $"{api.Status} {api.Message}"
                    : step == "authenticate" ? ex.GetType().FullName ?? ex.GetType().Name
                    : $"{ex.GetType().FullName}{(ex is InvalidOperationException ? ": " + ex.Message : "")}" + Environment.NewLine + ex.StackTrace);
            }
        });
        return endpoints;
    }

    /// <summary>A profile claim's first value. The provider may send one both in the identity token and in its userinfo (JetBrains Hub does
    /// for the name and the e-mail), and OpenIddict's GetClaim refuses a claim with several values.</summary>
    public static string? FirstClaim(ClaimsPrincipal principal, string type) => principal.GetClaims(type).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// Starts the provider side of a fresh re-authentication (WP4 V4): request_credentials=required (JetBrains Hub shows its login
    /// form), prompt=login and max_age=0 as authorization request parameters, and the challenge, account, stamp and Atlas session as
    /// state items. Returns the provider URL for the browser; the correlation cookie is set on this response.
    /// </summary>
    public static async Task<string> ReauthChallengeAsync(HttpContext context, AtlasOidcOptions settings, string providerId, Guid challengeId,
        Guid userId, string securityStamp, string sessionId, string? returnUrl)
    {
        var properties = ChallengeProperties(FindProvider(settings, providerId), returnUrl);
        properties.Items["atlas:operation"] = "reauth";
        properties.Items["atlas:challenge"] = challengeId.ToString();
        properties.Items["atlas:user"] = userId.ToString();
        properties.Items["atlas:stamp"] = securityStamp;
        properties.Items["atlas:session"] = sessionId;
        properties.Parameters["request_credentials"] = "required";
        properties.Parameters[OpenIddictConstants.Parameters.Prompt] = OpenIddictConstants.PromptValues.Login;
        properties.Parameters[OpenIddictConstants.Parameters.MaxAge] = "0";
        await ChallengeAsync(context, properties);
        var location = context.Response.Headers.Location.ToString();
        if (context.Response.StatusCode != 302 || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            throw new ApiException(503, "Poskytovatel přihlášení není dostupný.");
        }
        context.Response.Headers.Remove("Location");
        context.Response.StatusCode = 200;
        return location;
    }

    /// <summary>
    /// The callback of a re-authentication: never signs in. It needs the cookie session that started it (same account, stamp and
    /// Atlas session); the handler then checks the challenge, the linked identity and the id_token auth_time. auth_time is read from
    /// the validated backchannel id_token principal, not from the merged principal (which may also carry userinfo claims).
    /// </summary>
    private static async Task<IResult> CompleteReauthAsync(AtlasOidcProvider provider, AuthenticateResult result, string subject,
        ICurrentActor currentActor, IMessageBus bus, HttpContext context, CancellationToken ct)
    {
        var properties = result.Properties!;
        var target = OidcReturnUrl.Safe(properties.RedirectUri);
        var actor = await currentActor.RequireAsync(ct);
        if (!Guid.TryParse(properties.GetString("atlas:challenge"), out var challengeId)
            || properties.GetString("atlas:user") != actor.Id.ToString()
            || properties.GetString("atlas:stamp") != actor.SecurityStamp
            || string.IsNullOrEmpty(properties.GetString("atlas:session"))
            || properties.GetString("atlas:session") != context.User.FindFirstValue("atlas_session"))
        {
            return Results.Redirect(target);
        }
        var idToken = properties.GetParameter<ClaimsPrincipal>(OpenIddictClientAspNetCoreConstants.Properties.BackchannelIdentityTokenPrincipal)
            ?? properties.GetParameter<ClaimsPrincipal>(OpenIddictClientAspNetCoreConstants.Properties.FrontchannelIdentityTokenPrincipal);
        DateTime? authTime = long.TryParse(idToken?.GetClaim(OpenIddictConstants.Claims.AuthenticationTime), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 253402300800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
        await bus.InvokeAsync<Atlas.Server.Features.Security.Commands.ReauthOutcome>(
            new Atlas.Server.Features.Security.Commands.CompleteOidcReauth(challengeId, provider.Id, provider.Issuer, subject, authTime), ct);
        return Results.Redirect(target);
    }

    private static AtlasOidcProvider FindProvider(AtlasOidcOptions options, string id) => options.Providers.SingleOrDefault(p => p.Id == id) ?? throw new ApiException(404, "Poskytovatel přihlášení není dostupný.");

    private static AuthenticationProperties ChallengeProperties(AtlasOidcProvider provider, string? returnUrl) => new(new Dictionary<string, string?> { [OpenIddictClientAspNetCoreConstants.Properties.RegistrationId] = provider.Id }) { RedirectUri = OidcReturnUrl.Safe(returnUrl) };

    private static async Task ChallengeAsync(HttpContext context, AuthenticationProperties properties)
    {
        try
        {
            await context.ChallengeAsync(Scheme, properties);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILogger<AtlasOidcOptions>>().LogWarning("OIDC challenge failed ({ErrorType}).", ex.GetType().Name);
            throw new ApiException(503, "Poskytovatel přihlášení není dostupný. Zkuste to znovu později.");
        }
    }
}
