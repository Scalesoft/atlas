using Atlas.Server.Features.Security.Contracts;

namespace Atlas.Server.Features.Security.Queries;

/// <summary>GET /api/security/networks.</summary>
public sealed record ListTrustedNetworks;

/// <summary>GET /api/security/proxies.</summary>
public sealed record ListTrustedProxies;

/// <summary>POST /api/security/networks/test.</summary>
public sealed record TestTrustedAddress(TestAddressRequest Request);

/// <summary>GET /api/security/connection.</summary>
public sealed record GetConnection;

/// <summary>GET /api/security/reauth/{id}: a challenge of the caller's session.</summary>
public sealed record GetReauthStatus(Guid ChallengeId)
{
    public override string ToString() => "GetReauthStatus { sensitive values redacted }";
}

/// <summary>GET /api/security/reauth/self-test.</summary>
public sealed record GetFreshAuthProviders;
