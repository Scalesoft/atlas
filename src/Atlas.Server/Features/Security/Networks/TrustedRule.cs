namespace Atlas.Server.Features.Security.Networks;

/// <summary>A current (not archived) trusted network or proxy as loaded into a <see cref="NetworkConfiguration"/>.</summary>
public sealed record TrustedRule(Guid Id, string Purpose, string Name, string? Kind, AddressRange Range, bool IsActive);
