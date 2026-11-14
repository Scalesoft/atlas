using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class ListVaultLocationsHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultFolders m_folders;

    public ListVaultLocationsHandler(ISession session, VaultAccess access, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_folders = folders;
    }

    /// <summary>WikiAdmin sees roots and credentials (never the password); a space Admin sees names to choose from.</summary>
    public async Task<IReadOnlyList<VaultLocationView>> Handle(ListVaultLocations query, CancellationToken ct)
    {
        var (actor, spaces) = await m_access.ManagedSpacesAsync(ct);
        if (!actor.IsWikiAdmin && spaces is { Count: 0 })
        {
            throw new ApiException(403, "Trezory spravuje správce wiki nebo správce prostoru.");
        }
        var locations = await m_session.QueryOver<VaultLocation>().Where(l => l.ArchivedAt == null).ListAsync(ct);
        var counts = (await m_session.QueryOver<Vault>()
                .Where(v => v.ArchivedAt == null)
                .SelectList(list => list.SelectGroup(v => v.LocationId).SelectCount(v => v.Id))
                .ListAsync<object[]>(ct))
            .ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1], System.Globalization.CultureInfo.InvariantCulture));
        return locations
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => actor.IsWikiAdmin
                ? new VaultLocationView(l.Id, l.Name, l.Kind, m_folders.Root(l), l.Username, l.Domain, l.ProtectedPassword is not null, counts.GetValueOrDefault(l.Id))
                : new VaultLocationView(l.Id, l.Name, l.Kind, null, null, null, false, counts.GetValueOrDefault(l.Id)))
            .ToList();
    }
}
