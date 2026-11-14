using System.Net;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// What the connection itself says about a request's origin, before any forwarded header is applied: the TCP peer and the raw
/// X-Forwarded-For and X-Forwarded-Proto entries (left to right, across repeated header lines), and whether an RFC 7239 Forwarded
/// header came along. The transport middleware captures it first thing (<see cref="Capture"/>) and stores it as a request feature,
/// so security decisions resolve the client again from raw evidence against the current configuration version.
/// </summary>
public sealed record ClientAddressEvidence(IPAddress? Peer, IReadOnlyList<string> ForwardedFor, IReadOnlyList<string> ForwardedProto,
    bool ForwardedHeader)
{
    private const int MaxEntries = 32;

    public static ClientAddressEvidence Capture(HttpContext context)
    {
        var headers = context.Request.Headers;
        return new ClientAddressEvidence(context.Connection.RemoteIpAddress, Split(headers["X-Forwarded-For"]),
            Split(headers["X-Forwarded-Proto"]), headers.ContainsKey("Forwarded"));
    }

    /// <summary>The evidence the transport middleware captured, or the request as it is (hosts without that middleware).</summary>
    public static ClientAddressEvidence Of(HttpContext? context)
    {
        if (context is null)
        {
            return new ClientAddressEvidence(null, [], [], false);
        }
        return context.Features.Get<ClientAddressEvidence>() ?? Capture(context);
    }

    // Empty entries stay, so a malformed chain ("a,,b") is unclear rather than silently shortened. A very long chain keeps its
    // rightmost entries, the ones the trusted proxies appended.
    private static string[] Split(Microsoft.Extensions.Primitives.StringValues values)
    {
        var entries = values.SelectMany(value => (value ?? "").Split(',')).Select(entry => entry.Trim()).ToArray();
        if (entries.Length == 1 && entries[0].Length == 0)
        {
            return [];
        }
        return entries.Length > MaxEntries ? entries[^MaxEntries..] : entries;
    }
}
