using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Runtime;
using Atlas.Server.Platform.Storage;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class ListRefreshableVaultsHandler
{
    private readonly ISession m_session;
    private readonly VaultProtection m_protection;
    private readonly VaultFolders m_folders;

    public ListRefreshableVaultsHandler(ISession session, VaultProtection protection, VaultFolders folders)
    {
        m_session = session;
        m_protection = protection;
        m_folders = folders;
    }

    public async Task<IReadOnlyList<VaultTarget>> Handle(ListRefreshableVaults query, CancellationToken ct)
    {
        var vaults = await m_session.QueryOver<Vault>()
            .Where(v => v.ArchivedAt == null && v.ProtectedKdbxPassword != null && v.State != VaultStates.Locked)
            .ListAsync(ct);
        if (vaults.Count == 0)
        {
            return [];
        }
        var locations = (await m_session.QueryOver<VaultLocation>()
                .WhereRestrictionOn(l => l.Id).IsIn(vaults.Select(v => v.LocationId).Distinct().ToArray())
                .ListAsync(ct))
            .ToDictionary(l => l.Id);
        // A location password that no longer decrypts leaves the SMB login without one: the vault turns Unavailable.
        return vaults
            .Select(v =>
            {
                var location = locations[v.LocationId];
                var password = location.Kind == VaultLocation.SmbKind ? m_protection.UnprotectLocationPassword(location.ProtectedPassword) : null;
                return new VaultTarget(v.Id, new FileStorageOptions(m_folders.Root(location), location.Username, location.Domain, password), v.FileName, v.ProtectedKdbxPassword);
            })
            .ToList();
    }
}
