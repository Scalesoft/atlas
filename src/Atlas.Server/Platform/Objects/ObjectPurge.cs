using System.Text.Json.Nodes;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Objects;

/// <summary>
/// What every permanent delete shares (unified trash spec §2), whatever the type: the account's personal traces of the objects and
/// their shares, and the one audit row a purge leaves. The grants are access history: closed, never deleted (their audit records
/// the end with the purging actor). Comments go with their mentions and search documents (ON DELETE CASCADE).
/// </summary>
public static class ObjectPurge
{
    public const string SpaceTypeId = "atlas.space";

    /// <summary>The objects' shares, favorites, visits, comments, catalog occurrences, change log and the reference nodes pointing at
    /// them from any space's tree (PER3-5: a link to nothing is no lock to show), whose spaces' tree token moves; the envelope itself is
    /// the caller's (a module removes it with its row, a page keeps it as its tombstone's).</summary>
    public static async Task ForgetAsync(ISession session, IReadOnlyCollection<Guid> ids, Guid actorId, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }
        await session.FlushAsync(ct);
        // One literal (no interpolation), so IntegrityRulesConventionTests sees that it only closes open grants.
        var closed = await session.CreateSQLQuery("""
            UPDATE "ResourceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
              WHERE ("ObjectId" = ANY(CAST(string_to_array(:ids, ',') AS uuid[])) OR "PageId" = ANY(CAST(string_to_array(:ids, ',') AS uuid[])))
                AND "ActiveTo" IS NULL
            RETURNING "Id"
            """).SetParameter("ids", GuidList.Param(ids)).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actorId, ct);
        await session.CreateSQLQuery($"""
            DELETE FROM "PageFavorite" WHERE "PageId" = {GuidList.Any(":ids")};
            DELETE FROM "PageVisit" WHERE "PageId" = {GuidList.Any(":ids")};
            DELETE FROM "Comment" WHERE "ObjectId" = {GuidList.Any(":ids")};
            DELETE FROM "EntityOccurrence" WHERE "ResourceId" = {GuidList.Any(":ids")};
            DELETE FROM "ObjectChange" WHERE "ObjectId" = {GuidList.Any(":ids")};
            WITH node AS (DELETE FROM "TreeReference" WHERE "TargetId" = {GuidList.Any(":ids")} RETURNING "SpaceId")
              UPDATE "SpaceOrganization" SET "Version" = "Version" + 1 WHERE "SpaceId" IN (SELECT "SpaceId" FROM node);
            """).SetParameter("ids", GuidList.Param(ids)).ExecuteUpdateAsync(ct);
    }

    /// <summary>Every module's rows of the purged pages (IContentPurgeParticipant, enabled or not); the objects they count go into the
    /// audit's <paramref name="detail"/> (SEC3-6).</summary>
    public static async Task ParticipantsAsync(Resources.ContentParticipants participants, Guid spaceId, IReadOnlyCollection<Guid> pageIds, Guid? batchId, Guid actorId,
        JsonObject detail, CancellationToken ct)
    {
        foreach (var participant in participants.Purge)
        {
            var count = await participant.PurgeAsync(spaceId, pageIds, batchId, actorId, ct);
            if (participant.Counted is { } key)
            {
                detail[key] = count;
            }
        }
    }

    /// <summary>The audit of one permanent delete: who, when, what (type, space, title) and what went with it (<paramref name="detail"/>:
    /// counts, vault names), and the row of the access history the Security centre lists (object.purged, page.purged, space.purged; a
    /// private object's title stays out of it).</summary>
    public static async Task AuditAsync(ISession session, Guid objectId, string typeId, string scope, Guid? spaceId, string? title, Guid actorId,
        JsonObject? detail, CancellationToken ct)
    {
        var purge = Guid.NewGuid();
        await session.CreateSQLQuery("""
            INSERT INTO "ObjectPurgeEvent"("Id","ObjectId","TypeId","Scope","SpaceId","Title","PurgedById","PurgedAt","Detail")
            VALUES (:event, :id, :type, :scope, :space, :title, :actor, now(), CAST(:detail AS jsonb))
            """).SetParameter("event", purge).SetParameter("id", objectId).SetParameter("type", typeId).SetParameter("scope", scope)
            .SetParameter("space", spaceId, NHibernate.NHibernateUtil.Guid).SetParameter("title", Truncate(title), NHibernate.NHibernateUtil.String)
            .SetParameter("actor", actorId).SetParameter("detail", detail?.ToJsonString(), NHibernate.NHibernateUtil.String)
            .ExecuteUpdateAsync(ct);
        await AccessAuditLog.RecordAsync(session, actorId, typeId switch { "atlas.page" => "page.purged", SpaceTypeId => "space.purged", _ => "object.purged" },
            AccessAuditLog.ObjectPurgeEvent, purge, ct);
    }

    private static string? Truncate(string? title) => title is { Length: > 400 } ? title[..400] : title;
}
