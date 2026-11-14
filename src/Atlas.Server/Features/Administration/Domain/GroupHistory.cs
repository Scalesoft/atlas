using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Domain;

/// <summary>
/// The history of a group (UserGroupRevision) and its audit (group.created, group.renamed), in the transaction of the change with the
/// actor the caller passes. A writer of UserGroup calls <see cref="RecordAsync"/> after changing the entity and before the session
/// flushes it. Was TR_UserGroup_History and TR_UserGroupRevision_Audit until 3.6 (migration 2026110941).
/// </summary>
public static class GroupHistory
{
    public static async Task RecordAsync(ISession session, UserGroup group, Guid? actorId, CancellationToken ct)
    {
        if (session.ChangedProperties(group) is { Count: 0 })
        {
            return;
        }
        await session.FlushAsync(ct);
        var previous = await session.CreateSQLQuery("""
            UPDATE "UserGroupRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "GroupId" = :id AND "ActiveTo" IS NULL
            RETURNING "ActiveTo", "Name"
            """).SetParameter("id", group.Id).UniqueResultAsync<object[]>(ct);
        var revisionId = Guid.NewGuid();
        await session.CreateSQLQuery("""
            INSERT INTO "UserGroupRevision"("Id","GroupId","Name","ChangedBy","ActiveFrom","ActiveTo")
            VALUES (:revision, :id, :name, :actor, COALESCE(:boundary, clock_timestamp()), NULL)
            """)
            .SetParameter("revision", revisionId)
            .SetParameter("id", group.Id)
            .SetParameter("name", group.Name)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("boundary", previous?[0], NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
        if (previous is null || !Equals(previous[1], group.Name))
        {
            await AccessAuditLog.RecordAsync(session, actorId, previous is null ? "group.created" : "group.renamed", AccessAuditLog.GroupRevision, revisionId, ct);
        }
    }
}
