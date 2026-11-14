using Atlas.Server.Features.ContentImport.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentImport.Commands;

public sealed class CleanupExpiredImportSourcesHandler
{
    private readonly ISession m_session;
    private readonly ContentImportFiles m_files;
    private readonly ILogger<CleanupExpiredImportSourcesHandler> m_logger;

    public CleanupExpiredImportSourcesHandler(ISession session, ContentImportFiles files, ILogger<CleanupExpiredImportSourcesHandler> logger)
    {
        m_session = session;
        m_files = files;
        m_logger = logger;
    }

    public async Task Handle(CleanupExpiredImportSources command, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = await m_session.QueryOver<WikiImportBatch>()
            .Where(x => x.CompletedAt == null && x.ExpiresAt < now)
            .Select(x => x.Id)
            .ListAsync<Guid>(ct);
        foreach (var id in ids)
        {
            ImportSourceFiles.Remove(m_files, m_logger, id);
        }
    }
}
