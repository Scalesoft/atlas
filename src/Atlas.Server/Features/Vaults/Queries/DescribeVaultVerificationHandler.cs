using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class DescribeVaultVerificationHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultGrantSnapshots m_grants;

    public DescribeVaultVerificationHandler(ISession session, VaultAccess access, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_access = access;
        m_grants = grants;
    }

    public async Task<VaultVerification> Handle(DescribeVaultVerification query, CancellationToken ct)
    {
        var (actor, vault) = await m_access.RequireVaultAsync(query.VaultId, false, ct);
        var view = await VaultReads.ViewAsync(m_session, m_grants, actor, vault, ct);
        var snapshot = query.Snapshot;
        if (snapshot.State != VaultRuntimeState.Fresh)
        {
            return new VaultVerification(view, null, null, []);
        }
        var present = snapshot.Entries.Select(e => e.Uuid).ToHashSet();
        var registered = await m_session.QueryOver<VaultSecret>().Where(s => s.VaultId == vault.Id).ListAsync(ct);
        // Registry management is not Discover: titles are listed only as far as the actor may see the entries.
        var access = await m_grants.ForVaultsAsync(actor, [vault], ct);
        var missing = VaultReads.VisibleSecrets(vault, registered.Where(s => !present.Contains(s.EntryUuid)), access, DateTime.UtcNow)
            .OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new VaultVerification(view, snapshot.Entries.Count, VaultReads.Hex(snapshot.Sha256), missing);
    }
}
