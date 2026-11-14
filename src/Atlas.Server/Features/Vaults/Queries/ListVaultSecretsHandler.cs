using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class ListVaultSecretsHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;

    public ListVaultSecretsHandler(ISession session, VaultAccess access)
    {
        m_session = session;
        m_access = access;
    }

    /// <summary>404 unless the actor can Discover the vault; then the entries
    /// it may see (VaultReads.VisibleSecrets), decided over one grant snapshot.</summary>
    public async Task<IReadOnlyList<VaultSecretView>> Handle(ListVaultSecrets query, CancellationToken ct)
    {
        var target = await m_access.ReadVaultAsync(query.VaultId, false, ct);
        target.Authorize(VaultAccessLevel.Discover).Require();
        var secrets = await m_session.QueryOver<VaultSecret>().Where(s => s.VaultId == query.VaultId).ListAsync(ct);
        return VaultReads.VisibleSecrets(target.Vault!, secrets, target.Grants!, DateTime.UtcNow);
    }
}
