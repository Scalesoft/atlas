using System.Net;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Developer.Queries;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.Security.Networks;
using Wolverine;

namespace Atlas.Server.Features.Auth.Developer;

/// <summary>
/// "Přihlásit jako vývojář": a local build opens without credentials as the oldest active wiki administrator. Three independent
/// guards, all required: the route exists only in the Development environment, and every request asks again for that environment,
/// the Authentication:DeveloperSignIn:Enabled switch (true only in appsettings.Development.json) and a loopback TCP peer (or one in a
/// network named in Authentication:DeveloperSignIn:Networks) that sent no forwarded header. A refusal is the 404 of a route that does not exist, so the guard tells a caller nothing.
/// </summary>
public static class DeveloperSignIn
{
    public const string SwitchKey = "Authentication:DeveloperSignIn:Enabled";

    /// <summary>All three guards for this request; /api/auth/methods offers the button only when this holds.</summary>
    public static bool IsAllowed(HttpContext context)
    {
        var services = context.RequestServices;
        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment() || !services.GetRequiredService<IConfiguration>().GetValue(SwitchKey, false))
        {
            return false;
        }
        // The raw transport evidence, captured before forwarded headers rewrite the client address: a loopback peer that forwards
        // anything is a proxy on this host passing on someone else's request.
        var evidence = ClientAddressEvidence.Of(context);
        if (evidence.Peer is not { } raw || evidence.ForwardedFor.Count != 0 || evidence.ForwardedProto.Count != 0 || evidence.ForwardedHeader)
        {
            return false;
        }
        var peer = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
        // A development instance shared on an internal network may name its networks (CIDR); nothing is allowed beyond loopback otherwise.
        return IPAddress.IsLoopback(peer) || (services.GetRequiredService<IConfiguration>().GetSection(NetworksKey).Get<string[]>() ?? [])
            .Any(network => IPNetwork.TryParse(network, out var parsed) && parsed.Contains(peer));
    }

    /// <summary>Development only: networks (CIDR) besides loopback that may use the developer sign-in, e.g. the internal network of a shared development instance.</summary>
    public const string NetworksKey = "Authentication:DeveloperSignIn:Networks";

    public static void MapDeveloperSignIn(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return;
        }
        app.MapPost("/api/auth/developer", SignInAsync).RequireRateLimiting("authentication").Produces<UserDto>();
    }

    private static async Task<IResult> SignInAsync(IMessageBus bus, ISignInService signIn, HttpContext context, CancellationToken ct)
    {
        if (!IsAllowed(context))
        {
            return Results.NotFound();
        }
        var user = await bus.InvokeAsync<UserAccount>(new FindDeveloperAccount(), ct);
        await signIn.SignInAsync(context, user, "developer");
        return Results.Ok(await bus.InvokeAsync<UserDto>(new GetCurrentUser(), ct));
    }
}
