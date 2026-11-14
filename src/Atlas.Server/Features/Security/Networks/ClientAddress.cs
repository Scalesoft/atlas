using System.Net;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>How the client address was determined: directly from the TCP peer, through trusted proxies, or not at all.</summary>
public enum ClientAddressSource
{
    Direct,
    Forwarded,
    Unclear
}

/// <summary>
/// The verified client address of a request (<see cref="ClientAddressResolver"/>). Address is null when the origin is unclear; an
/// unclear origin never matches a trusted network. Reason: direct, forwarded, noPeer, untrustedForwarding (a peer that is not a
/// trusted proxy sent forwarding headers: a spoofing attempt or an unconfigured proxy), proxyWithoutForwardedFor, invalidForwardedFor
/// or hopLimitExceeded. Hops counts the trusted proxies passed; PeerIsProxy tells that the TCP peer is one.
/// </summary>
public sealed record ClientAddress(IPAddress? Address, ClientAddressSource Source, string Reason, IPAddress? Peer, bool PeerIsProxy, int Hops)
{
    public const string DirectReason = "direct";
    public const string ForwardedReason = "forwarded";
    public const string NoPeerReason = "noPeer";
    public const string UntrustedForwardingReason = "untrustedForwarding";
    public const string ProxyWithoutForwardedForReason = "proxyWithoutForwardedFor";
    public const string InvalidForwardedForReason = "invalidForwardedFor";
    public const string HopLimitExceededReason = "hopLimitExceeded";

    public bool Determined => Address is not null && Source != ClientAddressSource.Unclear;
}
