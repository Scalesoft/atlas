using Atlas.Server.Features.Security.Contracts;

namespace Atlas.Server.Features.Security.Commands;

/// <summary>Adds (Id null) or changes a trusted network or proxy. Purpose: network or proxy.</summary>
public sealed record SaveTrustedAddress(Guid? Id, string Purpose, SaveTrustedAddressRequest Request);

/// <summary>Removes (archives) a trusted network or proxy.</summary>
public sealed record ArchiveTrustedAddress(Guid Id, string Purpose, string? ExpectedVersion);

/// <summary>Sets how many trusted proxies a request may pass.</summary>
public sealed record SetProxyHopLimit(ProxyHopsRequest Request);
