using System.Net;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>CIDR parsing and normalization of trusted networks and proxies (spec 5.2), IPv4 and IPv6, and their warnings.</summary>
public sealed class AddressRangeTests
{
    [Theory]
    [InlineData("10.5.0.1", "10.5.0.1/32", false)]
    [InlineData(" 192.168.10.0/24 ", "192.168.10.0/24", false)]
    [InlineData("192.168.10.77/24", "192.168.10.0/24", true)]
    [InlineData("0.0.0.0/0", "0.0.0.0/0", false)]
    [InlineData("2001:db8::1", "2001:db8::1/128", false)]
    [InlineData("2001:DB8:0:0:0:0:0:0/32", "2001:db8::/32", false)]
    [InlineData("2001:db8:abcd::1/48", "2001:db8:abcd::/48", true)]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3/32", false)]
    [InlineData("::ffff:10.0.0.0/104", "10.0.0.0/8", false)]
    [InlineData("10.0.0.0/31", "10.0.0.0/31", false)]
    public void Ranges_are_normalized(string input, string expected, bool normalized)
    {
        var parsed = AddressRange.Parse(input);
        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.Range!.Text);
        Assert.Equal(normalized, parsed.Normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.1")]
    [InlineData("10")]
    [InlineData("010.0.0.1")]
    [InlineData("256.0.0.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("2001:db8::/129")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:10.0.0.0/64")]
    [InlineData("proxy.example.com")]
    [InlineData("*")]
    public void Invalid_ranges_are_refused_with_a_field_error(string input)
    {
        var parsed = AddressRange.Parse(input);
        Assert.Null(parsed.Range);
        Assert.False(string.IsNullOrEmpty(parsed.Error));
    }

    [Fact]
    public void Containment_respects_boundaries_families_and_mapped_addresses()
    {
        var office = AddressRange.Parse("192.168.10.0/24").Range!;
        Assert.True(office.Contains(IPAddress.Parse("192.168.10.0")));
        Assert.True(office.Contains(IPAddress.Parse("192.168.10.255")));
        Assert.False(office.Contains(IPAddress.Parse("192.168.11.0")));
        Assert.False(office.Contains(IPAddress.Parse("192.168.9.255")));
        Assert.True(office.Contains(IPAddress.Parse("::ffff:192.168.10.5")));
        Assert.False(office.Contains(IPAddress.Parse("2001:db8::1")));
        Assert.False(office.Contains(null));
        var vpn = AddressRange.Parse("2001:db8:10::/48").Range!;
        Assert.True(vpn.Contains(IPAddress.Parse("2001:db8:10:ffff::1")));
        Assert.False(vpn.Contains(IPAddress.Parse("2001:db8:11::1")));
        Assert.False(vpn.Contains(IPAddress.Parse("192.168.10.1")));
        var single = AddressRange.Parse("10.5.0.1").Range!;
        Assert.True(single.IsSingleAddress);
        Assert.False(single.Contains(IPAddress.Parse("10.5.0.2")));
    }

    [Fact]
    public void Warnings_report_normalization_duplicates_overlaps_and_the_whole_internet()
    {
        TrustedRule Rule(string name, string range, string purpose = TrustedAddress.NetworkPurpose)
            => new(Guid.NewGuid(), purpose, name, purpose == TrustedAddress.NetworkPurpose ? "lan" : null, AddressRange.Parse(range).Range!, true);
        var others = new[] { Rule("Kancelář", "192.168.10.0/24"), Rule("VPN", "10.8.0.0/16"), Rule("Proxy", "192.168.10.1/32", TrustedAddress.ProxyPurpose) };

        Assert.Empty(TrustedAddressRules.Warnings(TrustedAddress.NetworkPurpose, AddressRange.Parse("172.16.0.0/12"), others));
        var normalized = TrustedAddressRules.Warnings(TrustedAddress.NetworkPurpose, AddressRange.Parse("172.16.5.5/12"), others);
        Assert.Contains("172.16.0.0/12", Assert.Single(normalized));
        Assert.Contains("Kancelář", Assert.Single(TrustedAddressRules.Warnings(TrustedAddress.NetworkPurpose, AddressRange.Parse("192.168.10.0/24"), others)));
        // Overlap within the same purpose only: the proxy rule does not count against a network.
        var overlap = Assert.Single(TrustedAddressRules.Warnings(TrustedAddress.NetworkPurpose, AddressRange.Parse("192.168.10.128/25"), others));
        Assert.Contains("Překrývá", overlap);
        Assert.Contains(TrustedAddressRules.Warnings(TrustedAddress.NetworkPurpose, AddressRange.Parse("0.0.0.0/0"), others), w => w.Contains("celý internet"));
        Assert.Equal(400, Assert.Throws<ApiException>(() => TrustedAddressRules.Range(TrustedAddress.ProxyPurpose, "::/0")).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => TrustedAddressRules.Kind(TrustedAddress.NetworkPurpose, "wifi")).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => TrustedAddressRules.Kind(TrustedAddress.ProxyPurpose, "lan")).Status);
    }
}
