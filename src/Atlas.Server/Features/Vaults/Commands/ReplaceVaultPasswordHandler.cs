using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

public sealed class ReplaceVaultPasswordHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultGrantSnapshots m_grants;

    public ReplaceVaultPasswordHandler(ISession session, VaultAccess access, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_access = access;
        m_grants = grants;
    }

    public async Task<VaultView> Handle(ReplaceVaultPassword command, CancellationToken ct)
    {
        var (actor, vault) = await m_access.RequireVaultAsync(command.VaultId, true, ct);
        var now = DateTime.UtcNow;
        vault.ProtectedKdbxPassword = command.ProtectedKdbxPassword;
        vault.State = VaultStates.Unlocked;
        vault.LastFailure = null;
        vault.LastRefreshAt = now;
        vault.LastSnapshotSha256 = command.Read.Sha256;
        await VaultRegistry.SyncAsync(m_session, vault.Id, command.Read.Entries ?? [], now, ct);
        await m_session.FlushAsync(ct);
        return await VaultReads.ViewAsync(m_session, m_grants, actor, vault, ct);
    }
}
