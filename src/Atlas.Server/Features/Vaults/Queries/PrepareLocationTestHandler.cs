using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Platform.Storage;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

public sealed class PrepareLocationTestHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultProtection m_protection;
    private readonly VaultFolders m_folders;

    public PrepareLocationTestHandler(ISession session, VaultAccess access, VaultProtection protection, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_protection = protection;
        m_folders = folders;
    }

    public async Task<FileStorageOptions> Handle(PrepareLocationTest query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var request = query.Request;
        var (kind, root, username, domain) = VaultLocationRules.Normalize(request.Kind, request.Root, request.Username, request.Domain, m_folders);
        var password = kind == VaultLocation.SmbKind && !string.IsNullOrEmpty(request.Password) ? request.Password : null;
        if (password is null && kind == VaultLocation.SmbKind && request.LocationId is { } locationId)
        {
            var stored = await VaultRegistrations.RequireLocationAsync(m_session, locationId, ct);
            if (stored.ProtectedPassword is not null)
            {
                StoredCredential.Require(stored.Root, stored.Username, stored.Domain, root, username, domain);
            }
            password = VaultLocationRules.Storage(stored, m_protection, m_folders).Password;
        }
        if (kind == VaultLocation.ManagedKind)
        {
            // A new managed location has no folder yet: the test proves Atlas can write to the folder of managed locations.
            root = request.LocationId is { } managedId ? m_folders.Managed(managedId) : m_folders.ManagedRoot;
        }
        return new FileStorageOptions(root, username, domain, password);
    }
}
