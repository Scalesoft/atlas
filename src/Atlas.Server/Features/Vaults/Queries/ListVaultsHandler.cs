using Atlas.Server.Features.Vaults.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class ListVaultsHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultGrantSnapshots m_grants;

    public ListVaultsHandler(ISession session, VaultAccess access, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_access = access;
        m_grants = grants;
    }

    public async Task<IReadOnlyList<VaultView>> Handle(ListVaults query, CancellationToken ct)
    {
        var (actor, spaces) = await m_access.ManagedSpacesAsync(ct);
        return await VaultReads.ListAsync(m_session, m_grants, actor, spaces, ct);
    }
}
