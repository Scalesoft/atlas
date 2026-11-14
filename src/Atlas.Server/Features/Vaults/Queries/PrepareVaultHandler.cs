using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class PrepareVaultHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultProtection m_protection;
    private readonly VaultFolders m_folders;

    public PrepareVaultHandler(ISession session, VaultAccess access, VaultProtection protection, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_protection = protection;
        m_folders = folders;
    }

    public async Task<VaultTarget> Handle(PrepareVault query, CancellationToken ct)
    {
        var (_, vault) = await m_access.RequireVaultAsync(query.VaultId, false, ct);
        var location = await m_session.GetAsync<VaultLocation>(vault.LocationId, ct);
        return new VaultTarget(vault.Id, VaultLocationRules.Storage(location, m_protection, m_folders), vault.FileName, vault.ProtectedKdbxPassword);
    }
}
