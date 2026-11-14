using System.Net;
using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// One consistent version of the trusted networks and proxies (<see cref="SecurityConfiguration.Version"/>). Only active rules take
/// part in decisions; inactive ones are kept for the listings and the tester.
/// </summary>
public sealed record NetworkConfiguration(long Version, int HopLimit, IReadOnlyList<TrustedRule> Rules)
{
    public IReadOnlyList<AddressRange> ActiveProxies { get; } = Rules
        .Where(rule => rule.Purpose == TrustedAddress.ProxyPurpose && rule.IsActive).Select(rule => rule.Range).ToArray();

    public ClientAddress Resolve(ClientAddressEvidence evidence) => ClientAddressResolver.Resolve(evidence, ActiveProxies, HopLimit);

    /// <summary>The most specific active trusted network containing the address; none for a null (unclear) address.</summary>
    public TrustedRule? MatchNetwork(IPAddress? address)
    {
        return address is null ? null : Rules
            .Where(rule => rule.Purpose == TrustedAddress.NetworkPurpose && rule.IsActive && rule.Range.Contains(address))
            .OrderByDescending(rule => rule.Range.PrefixLength).ThenBy(rule => rule.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
