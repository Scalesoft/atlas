using Atlas.Server.Features.Vaults.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class PrepareNewVaultHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultProtection m_protection;
    private readonly VaultFolders m_folders;

    public PrepareNewVaultHandler(ISession session, VaultAccess access, VaultProtection protection, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_protection = protection;
        m_folders = folders;
    }

    /// <summary>A target with a fresh vault id and no stored password yet.</summary>
    public async Task<VaultTarget> Handle(PrepareNewVault query, CancellationToken ct)
    {
        await m_access.RequireSpaceAdminAsync(query.SpaceId, ct);
        var name = VaultRegistrations.RequireName(query.Name);
        var fileName = VaultRegistrations.RequireFileName(query.FileName);
        var location = await VaultRegistrations.RequireLocationAsync(m_session, query.LocationId, ct);
        await VaultRegistrations.EnsureUniqueAsync(m_session, query.SpaceId, location.Id, name, fileName, ct);
        return new VaultTarget(Guid.NewGuid(), VaultLocationRules.Storage(location, m_protection, m_folders), fileName, null);
    }
}
