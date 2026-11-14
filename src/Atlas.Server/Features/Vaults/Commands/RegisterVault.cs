using Atlas.Server.Features.Vaults.Runtime;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>
/// Records a vault whose file was just created or opened with the password. The password arrives already
/// protected (Data Protection); <paramref name="Read"/> carries the entry metadata for the first registry sync.
/// </summary>
public sealed record RegisterVault(Guid VaultId, Guid SpaceId, Guid LocationId, string Name, string FileName, string ProtectedKdbxPassword, VaultFileRead Read)
{
    public override string ToString() => $"Register vault {VaultId:N}";
}
