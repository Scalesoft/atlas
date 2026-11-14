using NHibernate;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Platform.Storage;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

public sealed class SaveVaultLocationHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultProtection m_protection;
    private readonly VaultFolders m_folders;

    public SaveVaultLocationHandler(ISession session, VaultAccess access, VaultProtection protection, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_protection = protection;
        m_folders = folders;
    }

    /// <summary>
    /// An empty password keeps the stored one; switching away from SMB drops the credentials. Changing the root
    /// of a used location retargets its vaults, which the next refresh checks. A managed location's folder is created
    /// on save, so its listing is empty rather than missing.
    /// </summary>
    public async Task<VaultLocationView> Handle(SaveVaultLocation command, CancellationToken ct)
    {
        var actor = await m_access.RequireWikiAdminAsync(ct);
        var request = command.Request;
        var (kind, root, username, domain) = VaultLocationRules.Normalize(request.Kind, request.Root, request.Username, request.Domain, m_folders);
        var location = command.LocationId is { } id
            ? await m_session.GetAsync<VaultLocation>(id, LockMode.Upgrade, ct)
            : new VaultLocation { Id = Guid.NewGuid() };
        if (location is null || location.ArchivedAt is not null)
        {
            throw new ApiException(404, "Umístění trezoru nebylo nalezeno.");
        }
        var name = Rules.Required(request.Name, "Název umístění", unchanged: location.Name);
        var names = await m_session.QueryOver<VaultLocation>()
            .Where(l => l.ArchivedAt == null && l.Id != location.Id)
            .Select(l => l.Name)
            .ListAsync<string>(ct);
        if (names.Any(existing => string.Equals(existing, name, StringComparison.InvariantCultureIgnoreCase)))
        {
            throw new ApiException(409, "Umístění s tímto názvem už existuje.", "vault_location_name_taken");
        }
        if (kind == VaultLocation.SmbKind && string.IsNullOrEmpty(request.Password) && location.ProtectedPassword is not null)
        {
            StoredCredential.Require(location.Root, location.Username, location.Domain, root, username, domain);
        }
        location.Name = name;
        location.Kind = kind;
        location.Root = root;
        location.Username = username;
        location.Domain = domain;
        if (kind != VaultLocation.SmbKind)
        {
            location.ProtectedPassword = null;
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            location.ProtectedPassword = m_protection.ProtectLocationPassword(request.Password);
        }
        if (command.LocationId is null)
        {
            await m_session.SaveAsync(location, ct);
        }
        await VaultLocationHistory.RecordAsync(m_session, location, actor.Id, ct);
        if (kind == VaultLocation.ManagedKind)
        {
            Directory.CreateDirectory(m_folders.Managed(location.Id));
        }
        var vaults = await m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null && v.LocationId == location.Id).RowCountAsync(ct);
        return new VaultLocationView(location.Id, location.Name, location.Kind, m_folders.Root(location), location.Username, location.Domain,
            location.ProtectedPassword is not null, vaults);
    }
}
