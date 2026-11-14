using System.Globalization;
using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AccessAudit.Queries;

public sealed class GetAccessAuditHandler
{
    private const long CoverageVersion = 22;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public GetAccessAuditHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<AccessAuditList> Handle(GetAccessAudit query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > 100)
        {
            throw new ApiException(400, "Audit lze načítat po 1 až 100 záznamech.");
        }
        long? before = null;
        if (query.BeforeId is not null)
        {
            if (!long.TryParse(query.BeforeId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new ApiException(400, "Neplatný kurzor auditu.");
            }
            before = value;
        }
        var actor = await m_access.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Audit přístupů je dostupný pouze správci wiki.");
        }
        var rows = m_session.QueryOver<AccessAuditView>();
        if (before is { } boundary)
        {
            rows = rows.Where(x => x.Id < boundary);
        }
        if (query.UserId is { } user)
        {
            rows = rows.Where(x => x.ActorId == user || x.UserId == user);
        }
        if (query.GroupId is { } group)
        {
            rows = rows.Where(x => x.GroupId == group);
        }
        if (query.SpaceId is { } space)
        {
            rows = rows.Where(x => x.SpaceId == space);
        }
        if (query.VaultId is { } vault)
        {
            rows = rows.Where(x => x.VaultId == vault);
        }
        // One row over the page size answers "has more" without a second count.
        var page = await rows.OrderBy(x => x.Id).Desc.Take(query.Limit + 1).ListAsync(ct);
        var items = page.Take(query.Limit).Select(x => x.ToDto()).ToArray();
        // A schema without the journal row reports the same 0001-01-01 the raw scalar produced.
        var coverage = await m_session.GetAsync<AccessAuditCoverage>(CoverageVersion, ct);
        return new AccessAuditList(items, page.Count > query.Limit, page.Count > query.Limit ? items[^1].Id : null, coverage?.AppliedAt ?? default);
    }
}
