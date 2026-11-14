using System.Security.Claims;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Security.Queries;
using Wolverine;

namespace Atlas.Server.Features.Security;

/// <summary>
/// Security centre API (WP4 V4): protection modes, trusted networks and proxies, connection diagnostics and fresh re-authentication.
/// Every route is authenticated, no-store and uncompressed, and /api/security and the vault and secret routes are not content
/// routes of RequestErrorHandling, so an unexpected failure logs only its type. Changes need a re-authentication proof in the
/// X-Atlas-Reauth header or the ten-minute window of an earlier one in the same session (ReauthProofs), and are refused with
/// ReauthRefusal otherwise; after a committed global change the transport reloads the trusted proxies.
/// No response or log line carries a password, a token or a proof.
/// </summary>
public static class SecurityEndpoints
{
    public static void MapSecurity(this IEndpointRouteBuilder app)
    {
        var security = app.MapGroup("/api/security").RequireAuthorization();
        security.MapGet("/protection", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ProtectionState>(new GetProtection(ProtectionLevel.InstanceLevel, null), ct)))
            .Produces<ProtectionState>();
        security.MapGet("/protection/overrides", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<ProtectionOverrideView>>(new ListProtectionOverrides(), ct)))
            .Produces<IReadOnlyList<ProtectionOverrideView>>();
        security.MapPut("/protection", (UpdateProtection request, IMessageBus bus, SecurityConfigurationCache cache, CancellationToken ct)
                => SetProtectionAsync(ProtectionLevel.InstanceLevel, null, request, bus, cache, ct))
            .Produces<ProtectionState>();
        app.MapGet("/api/vaults/{id:guid}/protection", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ProtectionState>(new GetProtection(ProtectionLevel.VaultLevel, id), ct)))
            .RequireAuthorization().Produces<ProtectionState>();
        app.MapPut("/api/vaults/{id:guid}/protection", (Guid id, UpdateProtection request, IMessageBus bus, SecurityConfigurationCache cache,
                CancellationToken ct) => SetProtectionAsync(ProtectionLevel.VaultLevel, id, request, bus, cache, ct))
            .RequireAuthorization().Produces<ProtectionState>();
        app.MapGet("/api/secrets/{id:guid}/protection", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ProtectionState>(new GetProtection(ProtectionLevel.SecretLevel, id), ct)))
            .RequireAuthorization().Produces<ProtectionState>();
        app.MapPut("/api/secrets/{id:guid}/protection", (Guid id, UpdateProtection request, IMessageBus bus, SecurityConfigurationCache cache,
                CancellationToken ct) => SetProtectionAsync(ProtectionLevel.SecretLevel, id, request, bus, cache, ct))
            .RequireAuthorization().Produces<ProtectionState>();

        security.MapGet("/networks", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<TrustedNetworkList>(new ListTrustedNetworks(), ct)))
            .Produces<TrustedNetworkList>();
        security.MapPost("/networks/test", async (TestAddressRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AddressTestResult>(new TestTrustedAddress(request), ct)))
            .Produces<AddressTestResult>();
        MapAddresses(security, "/networks", TrustedAddress.NetworkPurpose);
        security.MapGet("/proxies", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<TrustedProxyList>(new ListTrustedProxies(), ct)))
            .Produces<TrustedProxyList>();
        security.MapPut("/proxies/hops", async (ProxyHopsRequest request, IMessageBus bus, SecurityConfigurationCache cache, CancellationToken ct) =>
        {
            var version = await bus.InvokeAsync<string>(new SetProxyHopLimit(request), ct);
            cache.Invalidate();
            return Results.Ok(new { version });
        });
        MapAddresses(security, "/proxies", TrustedAddress.ProxyPurpose);
        security.MapGet("/connection", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ConnectionView>(new GetConnection(), ct)))
            .Produces<ConnectionView>();

        security.MapPost("/reauth", (StartReauthRequest request, IMessageBus bus, AtlasOidcOptions settings, HttpContext context, CancellationToken ct)
                => StartAsync(new StartReauth(request.Operation, request.VaultId, request.SecretId), request.ReturnUrl, bus, settings, context, ct))
            .RequireRateLimiting("authentication").Produces<ReauthStartView>();
        security.MapPost("/reauth/self-test", (StartSelfTestRequest request, IMessageBus bus, AtlasOidcOptions settings, HttpContext context,
                CancellationToken ct)
                => StartAsync(new StartReauth(ReauthChallenge.SelfTestOperation, null, null, request.ProviderId), request.ReturnUrl, bus, settings, context, ct))
            .RequireRateLimiting("authentication").Produces<ReauthStartView>();
        security.MapGet("/reauth/self-test", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<FreshAuthProviderView>>(new GetFreshAuthProviders(), ct)))
            .Produces<IReadOnlyList<FreshAuthProviderView>>();
        security.MapGet("/reauth/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ReauthStatusView>(new GetReauthStatus(id), ct)))
            .Produces<ReauthStatusView>();
        security.MapPost("/reauth/{id:guid}/password", VerifyPasswordAsync)
            .RequireRateLimiting("authentication").Produces<ReauthStatusView>();
    }

    private static void MapAddresses(RouteGroupBuilder security, string path, string purpose)
    {
        security.MapPost(path, async (SaveTrustedAddressRequest request, IMessageBus bus, SecurityConfigurationCache cache, CancellationToken ct) =>
        {
            var saved = await bus.InvokeAsync<TrustedAddressSaved>(new SaveTrustedAddress(null, purpose, request), ct);
            cache.Invalidate();
            return Results.Ok(saved);
        }).Produces<TrustedAddressSaved>();
        security.MapPut(path + "/{id:guid}", async (Guid id, SaveTrustedAddressRequest request, IMessageBus bus, SecurityConfigurationCache cache,
            CancellationToken ct) =>
        {
            var saved = await bus.InvokeAsync<TrustedAddressSaved>(new SaveTrustedAddress(id, purpose, request), ct);
            cache.Invalidate();
            return Results.Ok(saved);
        }).Produces<TrustedAddressSaved>();
        security.MapDelete(path + "/{id:guid}", async (Guid id, string? expectedVersion, IMessageBus bus, SecurityConfigurationCache cache,
            CancellationToken ct) =>
        {
            var version = await bus.InvokeAsync<string>(new ArchiveTrustedAddress(id, purpose, expectedVersion), ct);
            cache.Invalidate();
            return Results.Ok(new { version });
        });
    }

    private static async Task<IResult> SetProtectionAsync(string level, Guid? id, UpdateProtection request, IMessageBus bus,
        SecurityConfigurationCache cache, CancellationToken ct)
    {
        await bus.InvokeAsync(new SetProtection(level, id, request), ct);
        cache.Invalidate();
        // The state is read in its own transaction after the commit, so the cache only ever learns committed versions.
        return Results.Ok(await bus.InvokeAsync<ProtectionState>(new GetProtection(level, id), ct));
    }

    private static async Task<IResult> StartAsync(StartReauth command, string? returnUrl, IMessageBus bus, AtlasOidcOptions settings,
        HttpContext context, CancellationToken ct)
    {
        var started = await bus.InvokeAsync<ReauthStarted>(command, ct);
        string? redirect = null;
        string? providerName = null;
        if (started.Method == ReauthChallenge.OidcMethod)
        {
            providerName = settings.Providers.SingleOrDefault(p => p.Id == started.ProviderId)?.DisplayName;
            redirect = await OidcEndpoints.ReauthChallengeAsync(context, settings, started.ProviderId!, started.Id, started.UserId,
                started.SecurityStamp, started.SessionId, returnUrl);
        }
        return Results.Ok(new ReauthStartView(started.Id, started.Method, started.ProviderId, providerName, started.ExpiresAt, redirect));
    }

    /// <summary>The builtin login throttle of the account's signed-in password proofs (anonymous sign-ins never spend it) runs before
    /// any password work, and only a wrong password spends it; a wrong password answers 403, not 401, so the client keeps its
    /// session.</summary>
    private static async Task<IResult> VerifyPasswordAsync(Guid id, ReauthPasswordRequest request, IMessageBus bus, BuiltinLoginThrottle throttle,
        HttpContext context, CancellationToken ct)
    {
        var username = context.User.FindFirstValue(ClaimTypes.Name) ?? throw new ApiException(401, "Přihlaste se.");
        using var attempt = throttle.TryBegin(username, out var retryAfter);
        if (attempt is null)
        {
            context.Response.Headers.RetryAfter = BuiltinLoginThrottle.RetryAfterHeader(retryAfter);
            throw new ApiException(429, "Příliš mnoho pokusů o ověření hesla. Zkuste to za chvíli.");
        }
        var outcome = await bus.InvokeAsync<ReauthOutcome>(new VerifyReauthPassword(id, request.Password ?? ""), ct);
        if (!outcome.Verified)
        {
            if (outcome.Failure == "password")
            {
                attempt.Fail();
            }
            throw outcome.Failure == "password"
                ? new ApiException(403, "Heslo nesouhlasí.", "reauth_password")
                : new ApiException(404, "Ověření nebylo nalezeno nebo vypršelo. Začněte znovu.");
        }
        return Results.Ok(await bus.InvokeAsync<ReauthStatusView>(new GetReauthStatus(id), ct));
    }
}
