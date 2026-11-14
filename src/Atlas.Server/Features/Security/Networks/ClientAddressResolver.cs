using System.Net;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// Verified client address resolution (spec 5.2). Pure. Walks X-Forwarded-For from the right only while the current hop is a trusted
/// proxy, one entry per proxy, at most hopLimit proxies:
/// <list type="bullet">
/// <item>A peer that is not a trusted proxy is the client, unless it sent forwarding headers: then the origin is unclear, because
/// either the client spoofs them or an unconfigured proxy stands in front and its own address would pose as every client.</item>
/// <item>A trusted proxy never counts as the client: a chain that ends at one (no entry left, or more proxies than the hop limit)
/// is unclear, and so is an entry that is not a plain address.</item>
/// <item>Entries left of the first untrusted hop are whatever the client sent and are ignored.</item>
/// </list>
/// An empty proxy list trusts no forwarding at all. Unclear grants no network exception.
/// </summary>
public static class ClientAddressResolver
{
    public static ClientAddress Resolve(ClientAddressEvidence evidence, IReadOnlyCollection<AddressRange> proxies, int hopLimit)
    {
        var peer = AddressRange.Normalize(evidence.Peer);
        if (peer is null)
        {
            return new ClientAddress(null, ClientAddressSource.Unclear, ClientAddress.NoPeerReason, null, false, 0);
        }
        if (!IsProxy(peer, proxies))
        {
            return evidence.ForwardedFor.Count > 0 || evidence.ForwardedHeader
                ? new ClientAddress(null, ClientAddressSource.Unclear, ClientAddress.UntrustedForwardingReason, peer, false, 0)
                : new ClientAddress(peer, ClientAddressSource.Direct, ClientAddress.DirectReason, peer, false, 0);
        }
        var current = peer;
        var hops = 0;
        var index = evidence.ForwardedFor.Count - 1;
        while (IsProxy(current, proxies))
        {
            if (hops >= hopLimit)
            {
                return new ClientAddress(null, ClientAddressSource.Unclear, ClientAddress.HopLimitExceededReason, peer, true, hops);
            }
            if (index < 0)
            {
                return new ClientAddress(null, ClientAddressSource.Unclear, ClientAddress.ProxyWithoutForwardedForReason, peer, true, hops);
            }
            if (!TryParseEntry(evidence.ForwardedFor[index], out var next))
            {
                return new ClientAddress(null, ClientAddressSource.Unclear, ClientAddress.InvalidForwardedForReason, peer, true, hops);
            }
            current = next;
            index--;
            hops++;
        }
        return new ClientAddress(current, ClientAddressSource.Forwarded, ClientAddress.ForwardedReason, peer, true, hops);
    }

    public static bool IsProxy(IPAddress? address, IReadOnlyCollection<AddressRange> proxies)
    {
        return address is not null && proxies.Any(proxy => proxy.Contains(address));
    }

    /// <summary>An address, optionally with a port: 203.0.113.7, 203.0.113.7:4711, 2001:db8::1 or [2001:db8::1]:443.</summary>
    public static bool TryParseEntry(string entry, out IPAddress address)
    {
        address = IPAddress.None;
        var value = entry.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || (close + 1 < value.Length && !IsPort(value[(close + 1)..])))
            {
                return false;
            }
            return AddressRange.TryParseAddress(value[1..close], out address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon == value.LastIndexOf(':'))
        {
            // One colon: IPv4 with a port.
            return IsPort(value[colon..]) && AddressRange.TryParseAddress(value[..colon], out address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }
        return AddressRange.TryParseAddress(value, out address);
    }

    private static bool IsPort(string suffix)
    {
        return suffix.Length is > 1 and <= 6 && suffix[0] == ':' && suffix[1..].All(char.IsAsciiDigit)
            && int.Parse(suffix[1..], System.Globalization.CultureInfo.InvariantCulture) <= 65535;
    }
}
