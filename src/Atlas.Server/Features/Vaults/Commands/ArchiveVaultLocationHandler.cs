using NHibernate;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

public sealed class ArchiveVaultLocationHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;

    public ArchiveVaultLocationHandler(ISession session, VaultAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task Handle(ArchiveVaultLocation command, CancellationToken ct)
    {
        var actor = await m_access.RequireWikiAdminAsync(ct);
        var location = await m_session.GetAsync<VaultLocation>(command.LocationId, LockMode.Upgrade, ct);
        if (location is null || location.ArchivedAt is not null)
        {
            throw new ApiException(404, "Umístění trezoru nebylo nalezeno.");
        }
        // Registering a vault reads the location without a row lock; the FK and this count are the guard, and
        // a vault registered concurrently still points at an archived location it can keep using.
        var vaults = await m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null && v.LocationId == location.Id).RowCountAsync(ct);
        if (vaults > 0)
        {
            throw new ApiException(409, "Umístění používají trezory. Nejdříve je archivujte.", "vault_location_in_use");
        }
        location.ArchivedAt = DateTime.UtcNow;
        await VaultLocationHistory.RecordAsync(m_session, location, actor.Id, ct);
    }
}
