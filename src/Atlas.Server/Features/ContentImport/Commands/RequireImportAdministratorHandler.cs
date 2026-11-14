using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport.Commands;

public sealed class RequireImportAdministratorHandler
{
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly NHibernate.ISession m_session;

    public RequireImportAdministratorHandler(IContentAccess access, IAtlasLocks locks, NHibernate.ISession session)
    {
        m_access = access;
        m_locks = locks;
        m_session = session;
    }

    /// <summary>Short authorization transaction that runs before a 64 MiB upload reaches the disk.</summary>
    public async Task<Guid> Handle(RequireImportAdministrator command, CancellationToken ct)
    {
        var actor = await ImportGuards.RequireAdministratorAsync(m_session, m_access, m_locks, null, ct);
        return actor.Id;
    }
}
