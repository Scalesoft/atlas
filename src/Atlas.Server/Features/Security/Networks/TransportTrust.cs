namespace Atlas.Server.Features.Security.Networks;

/// <summary>The active trusted proxies and hop limit the request pipeline applies to X-Forwarded-For and X-Forwarded-Proto.</summary>
public sealed record TransportTrust(IReadOnlyList<AddressRange> Proxies, int HopLimit);
