namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>Detaches a vault from Atlas and forgets its stored password; the KDBX file and registry rows stay.</summary>
public sealed record ArchiveVault(Guid VaultId);
