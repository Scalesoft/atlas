using Atlas.Server.Features.Vaults.Runtime;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>Stores a master password that was just verified against the file, unlocks the vault and syncs the registry.</summary>
public sealed record ReplaceVaultPassword(Guid VaultId, string ProtectedKdbxPassword, VaultFileRead Read)
{
    public override string ToString() => $"Replace password of vault {VaultId:N}";
}
