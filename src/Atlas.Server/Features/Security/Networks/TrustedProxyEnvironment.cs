using System.Net;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// The environment side of proxy trust, used only for bootstrap and recovery. Addresses: the explicit Http:TrustedProxies list,
/// imported into the database once and trusted by the transport until the database has answered. Recover
/// (Http:RecoverTrustedProxies=true): at start the database proxy list is replaced by Addresses with one hop, audited as a system
/// change, and proxies cannot be edited in the GUI until the setting is removed again. Recovery changes only proxy trust; it never
/// disables authorization, fresh re-authentication or the audit.
/// </summary>
public sealed record TrustedProxyEnvironment(IReadOnlyList<IPAddress> Addresses, bool Recover)
{
    public IReadOnlyList<AddressRange> Ranges { get; } = Addresses
        .Select(address => AddressRange.Parse(AddressRange.Normalize(address)!.ToString()).Range!).DistinctBy(range => range.Text).ToArray();
}
