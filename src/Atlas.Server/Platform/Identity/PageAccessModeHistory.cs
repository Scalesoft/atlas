using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// The history of a page's access mode (PageAccessModeRevision: space defaults or own access), written in the transaction of the change
/// with the actor the caller passes. A writer that creates a page or changes Page.OwnAccess calls <see cref="RecordAsync"/> after
/// changing the entity; it compares the page's row with its current revision, so a flush in between loses nothing. The current
/// revision is the mode's CAS version (SetPageAccessModeHandler).
/// Was TR_Page_AccessModeHistory until 3.6 (migration 2026110941); RightsAuditPinTests keeps the rows as the trigger wrote them.
/// </summary>
public static class PageAccessModeHistory
{
    public static async Task RecordAsync(ISession session, Page page, Guid? actorId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        // One round trip decides and closes: an unchanged mode keeps its revision, a new page has none to close.
        var current = await session.CreateSQLQuery("""
            WITH current_revision AS (
                SELECT r."Id", r."OwnAccess" = p."OwnAccess" AS same FROM "PageAccessModeRevision" r JOIN "Page" p ON p."Id" = r."PageId"
                WHERE r."PageId" = :page AND r."ActiveTo" IS NULL),
            closed AS (
                UPDATE "PageAccessModeRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
                WHERE "Id" IN (SELECT "Id" FROM current_revision WHERE NOT same) AND "ActiveTo" IS NULL
                RETURNING "ActiveTo")
            SELECT COALESCE((SELECT bool_or(same) FROM current_revision), false) AS "Same", (SELECT max("ActiveTo") FROM closed) AS "Boundary"
            """).AddScalar("Same", NHibernateUtil.Boolean).AddScalar("Boundary", NHibernateUtil.UtcDateTime)
            .SetParameter("page", page.Id).UniqueResultAsync<object[]>(ct);
        if ((bool)current[0])
        {
            return;
        }
        var boundary = (DateTime?)current[1];
        await session.CreateSQLQuery("""
            INSERT INTO "PageAccessModeRevision"("Id","PageId","OwnAccess","ChangedBy","ActiveFrom","ActiveTo")
            VALUES (:id, :page, :own, :actor, COALESCE(:boundary, clock_timestamp()), NULL)
            """)
            .SetParameter("id", Guid.NewGuid())
            .SetParameter("page", page.Id)
            .SetParameter("own", page.OwnAccess)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
    }
}
