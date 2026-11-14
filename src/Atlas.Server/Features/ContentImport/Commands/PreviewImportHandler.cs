using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentImport.Commands;

public sealed class PreviewImportHandler
{
    private const long ImportLockKey = 731145019;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly ContentImportFiles m_files;
    private readonly ILogger<PreviewImportHandler> m_logger;

    public PreviewImportHandler(ISession session, IContentAccess access, IAtlasLocks locks, ContentImportFiles files, ILogger<PreviewImportHandler> logger)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_files = files;
        m_logger = logger;
    }

    public async Task<WikiImportReport> Handle(PreviewImport command, CancellationToken ct)
    {
        var options = WikiJsArchive.Validate(command.Options);
        var id = Guid.NewGuid();
        try
        {
            // Writing the ZIP to disk here (not in a separate message) is recorded deviation 4(e): the transaction
            // is briefly idle-in-transaction between this write and AcquireContentSharedAsync below.
            var hash = await m_files.WriteAsync(id, command.Input, ct);
            await using var stream = m_files.Open(id);
            if (options.StripPrefix is null && WikiJsArchive.SingleRoot(stream) is { } root && !await ImportedWithFolderAsync(options.SourceKey, root, ct))
            {
                // The batch stores these options, so the commit reads the archive the same way.
                options = options with { StripPrefix = root + "/" };
            }
            var archive = await WikiJsArchive.ReadAsync(stream, options, ct);
            await m_locks.AcquireContentSharedAsync(ct);
            await m_locks.AcquireAdvisoryAsync(ImportLockKey, ct);
            var sourceKey = options.SourceKey;
            var source = await m_session.QueryOver<WikiImportSource>().Where(x => x.SourceKey == sourceKey).SingleOrDefaultAsync(ct);
            if (source is null)
            {
                source = new WikiImportSource { Id = Guid.NewGuid(), SourceKey = sourceKey };
                await m_session.SaveAsync(source, ct);
            }
            var sourceId = source.Id;
            var destination = await m_session.QueryOver<WikiImportDestination>().Where(x => x.SourceId == sourceId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
            var actor = await ImportGuards.RequireAdministratorAsync(m_session, m_access, m_locks, destination?.SpaceId, ct);
            await ImportGuards.RequireQuarantineAsync(m_session, destination?.SpaceId, ct);
            if (destination is not null)
            {
                options = options with { SpaceName = (await m_session.GetAsync<Space>(destination.SpaceId, ct)).Name };
            }
            var (plans, planned) = await ImportPlanner.PlanAsync(m_session, archive, sourceId, options, null, ct);
            var now = DateTime.UtcNow;
            var report = ImportReportBuilder.Build(id, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), now.AddHours(24),
                destination?.SpaceId, options.SpaceName, planned, plans);
            await m_session.SaveAsync(new WikiImportBatch
            {
                Id = id,
                SourceId = sourceId,
                CreatedBy = actor.Id,
                CreatedAt = now,
                ExpiresAt = report.ExpiresAt,
                ArchiveSha256 = hash,
                OptionsJson = JsonSerializer.Serialize(options, Json),
                ReportJson = JsonSerializer.Serialize(report, Json)
            }, ct);
            await m_session.FlushAsync(ct);
            return report;
        }
        catch
        {
            // The middleware has not committed, so no batch row can reference this archive.
            // A failing COMMIT keeps the source on disk on purpose, exactly like the old commitStarted flag.
            ImportSourceFiles.Remove(m_files, m_logger, id);
            throw;
        }
    }

    /// <summary>
    /// A ZIP whose entries all sit in one folder is usually a downloaded repository, and the folder is not part
    /// of the wiki's paths. A source already imported with the folder in its page paths keeps it, so its pages stay matched.
    /// </summary>
    private async Task<bool> ImportedWithFolderAsync(string sourceKey, string root, CancellationToken ct)
    {
        var source = await m_session.QueryOver<WikiImportSource>().Where(x => x.SourceKey == sourceKey).SingleOrDefaultAsync(ct);
        if (source is null)
        {
            return false;
        }
        var sourceId = source.Id;
        var paths = await m_session.QueryOver<WikiImportPage>().Where(x => x.SourceId == sourceId).Select(x => x.SourcePath).ListAsync<string>(ct);
        return paths.Any(x => x.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }
}
