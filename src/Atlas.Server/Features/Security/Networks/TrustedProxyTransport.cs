namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// The forwarded-headers step of the pipeline (replaces the framework middleware): captures the raw <see cref="ClientAddressEvidence"/>
/// as a request feature, then applies the verified client address and the scheme the trusted proxies report. X-Forwarded-Host is
/// never honoured. An unclear chain leaves the TCP peer as the remote address.
/// </summary>
public static class TrustedProxyTransport
{
    public static void Apply(HttpContext context, ClientAddressEvidence evidence, TransportTrust trust)
    {
        context.Features.Set(evidence);
        var client = ClientAddressResolver.Resolve(evidence, trust.Proxies, trust.HopLimit);
        if (client.Source == ClientAddressSource.Forwarded)
        {
            context.Connection.RemoteIpAddress = client.Address;
            context.Connection.RemotePort = 0;
        }
        if (client.PeerIsProxy && evidence.ForwardedProto.Count > 0)
        {
            // The entry the last trusted proxy on the path appended, aligned from the right like X-Forwarded-For.
            var proto = evidence.ForwardedProto[Math.Max(0, evidence.ForwardedProto.Count - Math.Max(1, client.Hops))];
            if (string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase) || string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase))
            {
                context.Request.Scheme = proto.ToLowerInvariant();
            }
        }
    }
}
