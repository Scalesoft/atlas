using System.Net;
using Microsoft.AspNetCore.Http;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>Verified client address resolution behind trusted proxies (spec 5.2): spoofing from untrusted peers, a proxy's own address,
/// hop limits, malformed chains, an empty list, and the network decision on top.</summary>
public sealed class ClientAddressResolverTests
{
    private static readonly AddressRange[] OneProxy = [Range("10.0.0.10")];
    private static readonly AddressRange[] TwoProxies = [Range("10.0.0.10"), Range("10.0.1.0/24")];

    private static AddressRange Range(string text) => AddressRange.Parse(text).Range!;

    private static ClientAddressEvidence Evidence(string? peer, params string[] forwardedFor)
        => new(peer is null ? null : IPAddress.Parse(peer), forwardedFor, [], false);

    [Fact]
    public void Direct_client_without_forwarding_is_its_peer()
    {
        var client = ClientAddressResolver.Resolve(Evidence("203.0.113.7"), OneProxy, 1);
        Assert.Equal((ClientAddressSource.Direct, "203.0.113.7", ClientAddress.DirectReason), (client.Source, client.Address?.ToString(), client.Reason));
        Assert.True(client.Determined);
        Assert.Equal("192.168.10.5", ClientAddressResolver.Resolve(Evidence("::ffff:192.168.10.5"), [], 1).Address?.ToString());
    }

    [Theory]
    [InlineData("192.168.10.5")]
    [InlineData("10.0.0.10")]
    public void Forwarded_for_from_an_untrusted_peer_is_spoofing_or_an_unconfigured_proxy(string spoofed)
    {
        // The header claims an internal (or the proxy's) address; the peer is not a trusted proxy, so nothing is believed.
        var client = ClientAddressResolver.Resolve(Evidence("203.0.113.7", spoofed), OneProxy, 1);
        Assert.Equal((ClientAddressSource.Unclear, null, ClientAddress.UntrustedForwardingReason), (client.Source, client.Address, client.Reason));
        var forwardedOnly = ClientAddressResolver.Resolve(new ClientAddressEvidence(IPAddress.Parse("203.0.113.7"), [], [], true), OneProxy, 1);
        Assert.Equal(ClientAddress.UntrustedForwardingReason, forwardedOnly.Reason);
    }

    [Fact]
    public void Empty_proxy_list_trusts_no_forwarding()
    {
        var client = ClientAddressResolver.Resolve(Evidence("10.0.0.10", "192.168.10.5"), [], 5);
        Assert.Equal(ClientAddressSource.Unclear, client.Source);
        Assert.Null(client.Address);
    }

    [Fact]
    public void Trusted_proxy_forwards_the_rightmost_entry_and_ignores_what_the_client_prepended()
    {
        var client = ClientAddressResolver.Resolve(Evidence("10.0.0.10", "192.168.10.5", "10.0.0.10", "203.0.113.7:51234"), OneProxy, 1);
        Assert.Equal((ClientAddressSource.Forwarded, "203.0.113.7", 1), (client.Source, client.Address?.ToString(), client.Hops));
        var v6 = ClientAddressResolver.Resolve(Evidence("10.0.0.10", "[2001:db8::7]:443"), OneProxy, 1);
        Assert.Equal("2001:db8::7", v6.Address?.ToString());
    }

    [Fact]
    public void A_proxy_address_never_counts_as_the_client()
    {
        var noHeader = ClientAddressResolver.Resolve(Evidence("10.0.0.10"), OneProxy, 1);
        Assert.Equal((ClientAddressSource.Unclear, ClientAddress.ProxyWithoutForwardedForReason), (noHeader.Source, noHeader.Reason));
        // Two trusted proxies with a hop limit of one: the chain ends at a proxy.
        var tooDeep = ClientAddressResolver.Resolve(Evidence("10.0.0.10", "192.168.10.5", "10.0.1.20"), TwoProxies, 1);
        Assert.Equal((ClientAddressSource.Unclear, ClientAddress.HopLimitExceededReason, 1), (tooDeep.Source, tooDeep.Reason, tooDeep.Hops));
        var chainEnds = ClientAddressResolver.Resolve(Evidence("10.0.0.10", "10.0.1.20"), TwoProxies, 2);
        Assert.Equal(ClientAddress.ProxyWithoutForwardedForReason, chainEnds.Reason);
    }

    [Fact]
    public void Hop_limit_allows_exactly_the_configured_proxies()
    {
        var evidence = Evidence("10.0.0.10", "spoofed", "192.168.10.5", "10.0.1.20");
        var client = ClientAddressResolver.Resolve(evidence, TwoProxies, 2);
        Assert.Equal((ClientAddressSource.Forwarded, "192.168.10.5", 2), (client.Source, client.Address?.ToString(), client.Hops));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("192.168.10.5:99999")]
    [InlineData("_hidden")]
    [InlineData("10.1")]
    public void A_malformed_entry_from_a_trusted_proxy_is_unclear(string entry)
    {
        var client = ClientAddressResolver.Resolve(Evidence("10.0.0.10", entry), OneProxy, 1);
        Assert.Equal((ClientAddressSource.Unclear, ClientAddress.InvalidForwardedForReason), (client.Source, client.Reason));
    }

    [Fact]
    public void No_peer_is_unclear()
    {
        Assert.Equal(ClientAddress.NoPeerReason, ClientAddressResolver.Resolve(Evidence(null), OneProxy, 1).Reason);
    }

    [Fact]
    public void Network_decision_uses_only_active_rules_and_never_an_unclear_origin()
    {
        TrustedRule Rule(string name, string range, bool active = true, string purpose = TrustedAddress.NetworkPurpose)
            => new(Guid.NewGuid(), purpose, name, purpose == TrustedAddress.NetworkPurpose ? "lan" : null, Range(range), active);
        var configuration = new NetworkConfiguration(7, 1, [
            Rule("Kancelář", "192.168.10.0/24"), Rule("Kancelář – tiskárny", "192.168.10.128/25"), Rule("Stará VPN", "10.8.0.0/16", false),
            Rule("Reverzní proxy", "10.0.0.10", true, TrustedAddress.ProxyPurpose)]);
        Assert.Equal("Kancelář – tiskárny", configuration.MatchNetwork(IPAddress.Parse("192.168.10.200"))!.Name);
        Assert.Equal("Kancelář", configuration.MatchNetwork(IPAddress.Parse("192.168.10.5"))!.Name);
        Assert.Null(configuration.MatchNetwork(IPAddress.Parse("10.8.0.5")));
        // The proxy's own address is no trusted network, and a spoofed internal address resolves to nothing.
        Assert.Null(configuration.MatchNetwork(IPAddress.Parse("10.0.0.10")));
        var spoofed = configuration.Resolve(Evidence("203.0.113.7", "192.168.10.5"));
        Assert.Null(configuration.MatchNetwork(spoofed.Determined ? spoofed.Address : null));
        var viaProxy = configuration.Resolve(Evidence("10.0.0.10", "192.168.10.5"));
        Assert.Equal("Kancelář", configuration.MatchNetwork(viaProxy.Address)!.Name);
        Assert.Null(new NetworkConfiguration(1, 1, []).MatchNetwork(IPAddress.Parse("192.168.10.5")));
    }

    [Fact]
    public void Transport_applies_the_verified_address_and_scheme_only_through_a_trusted_proxy()
    {
        var trust = new TransportTrust(OneProxy, 1);
        var proxied = new DefaultHttpContext();
        proxied.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.10");
        proxied.Request.Headers["X-Forwarded-For"] = "192.168.10.5, 203.0.113.7";
        proxied.Request.Headers["X-Forwarded-Proto"] = "https";
        proxied.Request.Headers["X-Forwarded-Host"] = "evil.example";
        TrustedProxyTransport.Apply(proxied, ClientAddressEvidence.Capture(proxied), trust);
        Assert.Equal(("203.0.113.7", "https"), (proxied.Connection.RemoteIpAddress!.ToString(), proxied.Request.Scheme));
        // The raw evidence stays available for security decisions.
        Assert.Equal("10.0.0.10", ClientAddressEvidence.Of(proxied).Peer!.ToString());

        var direct = new DefaultHttpContext();
        direct.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        direct.Request.Scheme = "http";
        direct.Request.Headers["X-Forwarded-For"] = "192.168.10.5";
        direct.Request.Headers["X-Forwarded-Proto"] = "https";
        TrustedProxyTransport.Apply(direct, ClientAddressEvidence.Capture(direct), trust);
        Assert.Equal(("203.0.113.9", "http"), (direct.Connection.RemoteIpAddress!.ToString(), direct.Request.Scheme));
    }
}
