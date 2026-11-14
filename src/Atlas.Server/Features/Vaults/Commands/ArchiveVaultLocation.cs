namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>Removes an unused location from the choices; its history stays.</summary>
public sealed record ArchiveVaultLocation(Guid LocationId);
