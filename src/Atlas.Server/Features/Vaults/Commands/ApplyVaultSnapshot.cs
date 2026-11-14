using Atlas.Server.Features.Vaults.Runtime;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>
/// System command of the refresh: persists the outcome of one check. Ignored when the vault was archived, its
/// password was replaced since the check started (<paramref name="ExpectedProtectedPassword"/>) or a newer
/// state exists in this process.
/// </summary>
public sealed record ApplyVaultSnapshot(Guid VaultId, string? ExpectedProtectedPassword, VaultSnapshot Snapshot)
{
    public override string ToString() => $"Apply snapshot of vault {VaultId:N}";
}
