namespace Atlas.Server.Features.Security.Contracts;

/// <summary>A trusted network (Kind lan, vpn or other) or proxy (Kind null) with its normalized Range and the warnings it raises
/// against the other current rules (duplicates, overlaps, the whole internet).</summary>
public sealed record TrustedAddressView(Guid Id, string Name, string? Kind, string Range, bool IsActive, IReadOnlyList<string> Warnings);

/// <summary>GET /api/security/networks. Version is the security configuration version, the CAS token of every network, proxy and
/// hop limit change.</summary>
public sealed record TrustedNetworkList(string Version, IReadOnlyList<TrustedAddressView> Items);

/// <summary>The environment side of proxy trust, shown so nothing differs hidden from the GUI: Addresses from Http:TrustedProxies
/// (imported once at ImportedAt, never applied again), Differs when they disagree with the active proxies, Recovery when
/// Http:RecoverTrustedProxies is on (proxies are read-only then).</summary>
public sealed record ProxyEnvironmentView(IReadOnlyList<string> Addresses, bool Differs, bool Recovery, DateTime? ImportedAt);

/// <summary>GET /api/security/proxies: the proxies, the hop limit (1 to MaxHopLimit) and the environment.</summary>
public sealed record TrustedProxyList(string Version, int HopLimit, int MaxHopLimit, IReadOnlyList<TrustedAddressView> Items,
    ProxyEnvironmentView Environment);

/// <summary>POST/PUT of a network or proxy. Range: an IPv4/IPv6 address or CIDR. Kind: lan, vpn or other (networks only). IsActive
/// defaults to true. ExpectedVersion: the configuration Version the change was prepared against.</summary>
public sealed record SaveTrustedAddressRequest(string? Name, string? Kind, string? Range, bool? IsActive, string? ExpectedVersion);

/// <summary>A saved rule, its warnings and the configuration version it activated.</summary>
public sealed record TrustedAddressSaved(Guid Id, IReadOnlyList<string> Warnings, string Version);

/// <summary>PUT /api/security/proxies/hops.</summary>
public sealed record ProxyHopsRequest(int HopLimit, string? ExpectedVersion);

/// <summary>A rule an address falls into.</summary>
public sealed record TrustedMatchView(Guid Id, string Name, string? Kind, string Range);

/// <summary>POST /api/security/networks/test: an address against the current rules and, optionally, a draft range (ExcludeId: the
/// rule the draft replaces). Not an authorization proof.</summary>
public sealed record TestAddressRequest(string? Address, string? Range, Guid? ExcludeId);

/// <summary>A draft range: normalized form, parse error, whether it contains the tested address and its warnings.</summary>
public sealed record DraftRangeView(string? Range, bool Normalized, string? Error, bool ContainsAddress, IReadOnlyList<string> Warnings);

/// <summary>The tester's answer: the normalized address (or its error), whether it is a trusted proxy, the active network it would
/// match, inactive networks that contain it, and the draft.</summary>
public sealed record AddressTestResult(string? Address, string? AddressError, bool IsProxy, TrustedMatchView? Network,
    IReadOnlyList<TrustedMatchView> InactiveMatches, DraftRangeView? Draft);
