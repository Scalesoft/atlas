namespace Atlas.Server.Features.Vaults.Commands;

public sealed class ArchiveVaultHandler
{
    private readonly VaultAccess m_access;
    private readonly AccessChanges m_accessChanges;

    public ArchiveVaultHandler(VaultAccess access, AccessChanges accessChanges)
    {
        m_access = access;
        m_accessChanges = accessChanges;
    }

    /// <summary>The stored password is erased with the archive; the caller drops the in-memory key after the commit.</summary>
    public async Task Handle(ArchiveVault command, CancellationToken ct)
    {
        var (_, vault) = await m_access.RequireVaultAsync(command.VaultId, true, ct);
        vault.ArchivedAt = DateTime.UtcNow;
        vault.ProtectedKdbxPassword = null;
        vault.State = Domain.VaultStates.Locked;
        // Nobody reads its secrets any more: live sessions and released values are re-authorized after the commit (WP4 V5).
        m_accessChanges.Changed();
    }
}
