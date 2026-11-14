using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class PrepareLocationListingHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultProtection m_protection;
    private readonly VaultFolders m_folders;

    public PrepareLocationListingHandler(ISession session, VaultAccess access, VaultProtection protection, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_protection = protection;
        m_folders = folders;
    }

    public async Task<LocationListingTarget> Handle(PrepareLocationListing query, CancellationToken ct)
    {
        IReadOnlySet<Guid>? managed;
        if (query.SpaceId is { } spaceId)
        {
            await m_access.RequireSpaceAdminAsync(spaceId, ct);
            managed = (await m_access.ManagedSpacesAsync(ct)).Spaces;
        }
        else
        {
            await m_access.RequireWikiAdminAsync(ct);
            managed = null;
        }
        var location = await VaultRegistrations.RequireLocationAsync(m_session, query.LocationId, ct);
        var vaults = await m_session.QueryOver<Vault>()
            .Where(v => v.ArchivedAt == null && v.LocationId == location.Id)
            .ListAsync(ct);
        var registered = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var vault in vaults)
        {
            registered[vault.FileName] = managed is null || managed.Contains(vault.SpaceId) ? vault.Name : null;
        }
        return new LocationListingTarget(VaultLocationRules.Storage(location, m_protection, m_folders), registered);
    }
}
