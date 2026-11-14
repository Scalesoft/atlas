using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Atlas.Server.Platform.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Atlas.Server.Features.PersonalApi;

public sealed class PersonalApiAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Atlas.PersonalApi";
    /// <summary>The effective scopes: the token's scopes within the owner's current level.</summary>
    public const string ScopeClaim = "api_scope";
    /// <summary>The token's own scopes, only to word a refusal (missing scope vs. lowered ceiling).</summary>
    public const string GrantedScopeClaim = "api_scope_granted";
    public const string ChannelClaim = "api_channel";
    public const string TokenClaim = "api_token_id";
    private readonly PersonalApiCredentials m_credentials;

    public PersonalApiAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, PersonalApiCredentials credentials) : base(options, logger, encoder)
    {
        m_credentials = credentials;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The server decides the channel from the path; nothing the client sends can choose it.
        if (ApiChannels.ForPath(Request.Path) is not { } channel
            || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
        {
            return AuthenticateResult.NoResult();
        }
        var result = await m_credentials.AuthenticateAsync(header.Parameter, channel, Context.RequestAborted);
        if (result is null)
        {
            return AuthenticateResult.Fail("Invalid personal API credential.");
        }
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, result.ActorId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, result.Username));
        identity.AddClaim(new Claim("stamp", result.SecurityStamp));
        identity.AddClaim(new Claim(TokenClaim, result.TokenId.ToString()));
        identity.AddClaim(new Claim(ChannelClaim, result.Channel));
        foreach (var scope in result.Scopes)
        {
            identity.AddClaim(new Claim(ScopeClaim, scope));
        }
        foreach (var scope in result.GrantedScopes)
        {
            identity.AddClaim(new Claim(GrantedScopeClaim, scope));
        }
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer realm=\"Atlas\"";
        await ProblemResponse.WriteAsync(Context, 401, "Platný osobní API token je vyžadován.");
    }

    /// <summary>403 scope_missing, worded by whether the token never had the scope the endpoint requires or the ceiling removed it.</summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => ProblemResponse.WriteAsync(Context, 403, ForbiddenTitle(Context), "scope_missing");

    internal static string ForbiddenTitle(HttpContext context)
    {
        var policies = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToArray() ?? [];
        var required = ApiScopes.All.LastOrDefault(scope => policies.Contains(PersonalApiEndpoints.PolicyFor(scope)));
        return RefusalTitle(context.User, required);
    }

    /// <summary>The refusal of a missing scope, worded by whether the token never had it or the ceiling removed it. Shared by REST
    /// (403 scope_missing) and the MCP tool filter (tool error scope_missing).</summary>
    internal static string RefusalTitle(ClaimsPrincipal user, string? required)
    {
        if (required is null)
        {
            return "Token nemá požadovaný rozsah oprávnění.";
        }
        var effective = user.FindAll(ScopeClaim).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
        var granted = user.FindAll(GrantedScopeClaim).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
        if (!granted.Contains(required) || effective.Contains(required))
        {
            return $"Token nemá rozsah {ApiScopes.Label(required)}.";
        }
        var remaining = ApiScopes.Ordered(effective).Select(scope => ApiScopes.Label(scope).ToLowerInvariant());
        return $"Správce wiki vám snížil oprávnění API; token teď smí jen {string.Join(" a ", remaining)}.";
    }
}
