namespace Atlas.Server.Features.Security.Contracts;

/// <summary>
/// "Vaše připojení" (GET /api/security/connection, WikiAdmin): the verified client Address (null when unclear), how it was derived
/// (Source direct, forwarded or unclear, with Reason), the TCP Peer and whether it is a trusted proxy, the proxies passed, the raw
/// X-Forwarded-For entries, the trusted Network that matched, whether it waives re-authentication in outsideTrustedNetworks, and
/// what the instance default mode means for this connection. Diagnostics only, never an authorization proof.
/// </summary>
public sealed record ConnectionView(string? Address, string Source, string Reason, string? Peer, bool PeerIsProxy, int Hops,
    IReadOnlyList<string> ForwardedFor, TrustedMatchView? Network, bool NetworkException, string InstanceMode, bool ReauthRequired,
    string Version);
