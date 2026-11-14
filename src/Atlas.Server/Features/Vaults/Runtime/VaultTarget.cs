using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// Everything needed to reach one vault file outside a database transaction: the storage (with the decrypted SMB
/// password, in memory only), the file name and the protected KDBX password for automatic unlock.
/// </summary>
public sealed record VaultTarget(Guid VaultId, FileStorageOptions Storage, string FileName, string? ProtectedKdbxPassword)
{
    public override string ToString() => $"Vault target {VaultId:N} (credentials omitted)";
}
