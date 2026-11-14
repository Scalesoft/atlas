using System.Text.Json;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentImport.Queries;

public sealed class GetImportReportHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public GetImportReportHandler(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    public async Task<WikiImportReport> Handle(GetImportReport query, CancellationToken ct)
    {
        await ImportGuards.RequireAdministratorAsync(m_session, m_access, m_locks, null, ct);
        var batch = await m_session.GetAsync<WikiImportBatch>(query.Id, ct) ?? throw new ApiException(404, "Import nebyl nalezen.");
        return JsonSerializer.Deserialize<WikiImportReport>(batch.ReportJson, Json)!;
    }

    public async Task<DownloadLease> Handle(RevalidateImportDownload query, CancellationToken ct)
    {
        var actor = await ImportGuards.RequireAdministratorAsync(m_session, m_access, m_locks, null, ct);
        var batch = await m_session.GetAsync<WikiImportBatch>(query.Id, ct) ?? throw new ApiException(404, "Import nebyl nalezen.");
        var report = JsonSerializer.Deserialize<WikiImportReport>(batch.ReportJson, Json)!;
        var authority = (await EffectiveWikiAdmin.ReadAsync(m_session, actor, ct)).Authority(DateTime.UtcNow);
        if (!authority.Allowed) throw new ApiException(403, "Platnost oprávnění ke stažení skončila.");
        var lease = new DownloadLease(authority.ExpiresAt);
        if (query.Source && report.CompletedAt is null)
        {
            if (report.ExpiresAt <= DateTime.UtcNow) throw new ApiException(410, "Zdroj náhledu vypršel.");
            lease = lease.Limit(report.ExpiresAt);
        }
        return lease;
    }
}
