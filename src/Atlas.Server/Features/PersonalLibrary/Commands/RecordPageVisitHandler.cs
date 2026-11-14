using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalLibrary.Commands;

public sealed class RecordPageVisitHandler
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);
    private readonly ISession m_session;
    private readonly PersonalPageGuard m_guard;

    public RecordPageVisitHandler(ISession session, PersonalPageGuard guard)
    {
        m_session = session;
        m_guard = guard;
    }

    public async Task<PersonalPageState> Handle(RecordPageVisit command, CancellationToken ct)
    {
        var pageId = command.PageId;
        var (actor, isPage) = await m_guard.RequireResourceAsync(pageId, true, ct);
        var actorId = actor.Id;
        var visit = await m_session.QueryOver<PageVisit>().Where(x => x.UserId == actorId && x.PageId == pageId).SingleOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        // The personal write lock also serializes the durable throttle after navigation retention. Readership counts pages only.
        if (isPage)
        {
            await m_session.CreateSQLQuery("""
                INSERT INTO "PageView" ("Id", "PageId", "UserId", "ViewedAt")
                SELECT :id, :page, :actor, :now
                WHERE NOT EXISTS (
                    SELECT 1 FROM "PageView"
                    WHERE "PageId"=:page AND "UserId"=:actor AND "ViewedAt">:cutoff)
                """)
                .SetParameter("id", Guid.NewGuid()).SetParameter("page", pageId).SetParameter("actor", actorId)
                .SetParameter("now", now).SetParameter("cutoff", now - Throttle).ExecuteUpdateAsync(ct);
        }
        if (visit is null || now - visit.LastVisitedAt >= Throttle)
        {
            if (visit is null)
            {
                await m_session.SaveAsync(new PageVisit { Id = Guid.NewGuid(), UserId = actorId, PageId = pageId, LastVisitedAt = now }, ct);
            }
            else
            {
                visit.LastVisitedAt = now;
            }
            // The newest 100 per account stay: OperationalRetention (PageVisit) trims the rest hourly.
            await m_session.FlushAsync(ct);
        }
        return await m_guard.ReadStateAsync(pageId, actorId, ct);
    }
}
