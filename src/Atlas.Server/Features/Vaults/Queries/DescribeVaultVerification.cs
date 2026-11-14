using Atlas.Server.Features.Vaults.Runtime;

namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Compares a snapshot just read with the registry of the vault.</summary>
public sealed record DescribeVaultVerification(Guid VaultId, VaultSnapshot Snapshot);
