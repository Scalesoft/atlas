using Atlas.Server.Features.Vaults.Contracts;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>Creates a location (no id) or updates one.</summary>
public sealed record SaveVaultLocation(Guid? LocationId, SaveVaultLocationRequest Request);
