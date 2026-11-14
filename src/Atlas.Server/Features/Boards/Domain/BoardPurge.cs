using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>The rows of permanently deleted boards (unified trash spec §2): the shared traces of the object (ObjectPurge), then its
/// search document, operation log, items, the board and its envelope. The caller audits.</summary>
public static class BoardPurge
{
    public static async Task DeleteAsync(ISession session, IReadOnlyCollection<Guid> ids, Guid actorId, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }
        await ObjectPurge.ForgetAsync(session, ids, actorId, ct);
        await session.CreateSQLQuery($"""
            DELETE FROM "BoardSearchDocument" WHERE "BoardId" = {GuidList.Any(":ids")};
            DELETE FROM "BoardOperation" WHERE "BoardId" = {GuidList.Any(":ids")};
            DELETE FROM "BoardItem" WHERE "BoardId" = {GuidList.Any(":ids")};
            WITH board AS (DELETE FROM "Board" WHERE "Id" = {GuidList.Any(":ids")} RETURNING "Id")
              DELETE FROM "ContentResource" WHERE "Id" IN (SELECT "Id" FROM board);
            """).SetParameter("ids", GuidList.Param(ids)).ExecuteUpdateAsync(ct);
    }
}

/// <summary>The boards of permanently deleted pages and spaces, with the module on or off (ARCH3-2): those under the pages that went to
/// the trash with their batch, a page's own board whatever its batch, and a live one left under them while the module was off; with a
/// whole space every board of it.</summary>
public sealed class BoardPagePurge(ISession session) : IContentPurgeParticipant
{
    public string Counted => "boards";

    public async Task<int> PurgeAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, Guid? batchId, Guid actorId, CancellationToken ct)
    {
        if (batchId is not null && pageIds.Count == 0)
        {
            return 0;
        }
        await session.FlushAsync(ct);
        var query = session.CreateSQLQuery(batchId is null ? """SELECT "Id" FROM "Board" WHERE "SpaceId"=:space""" : $"""
                SELECT "Id" FROM "Board" WHERE "SpaceId"=:space AND "ParentPageId" = {GuidList.Any(":pages")}
                  AND ("TrashBatchId"=:batch OR "Purpose" IS NOT NULL OR NOT "IsTrashed")
                """).AddScalar("Id", NHibernateUtil.Guid).SetParameter("space", spaceId);
        if (batchId is { } batch)
        {
            query = query.SetParameter("pages", GuidList.Param(pageIds)).SetParameter("batch", batch);
        }
        var ids = await query.ListAsync<Guid>(ct);
        await BoardPurge.DeleteAsync(session, ids.ToList(), actorId, ct);
        return ids.Count;
    }
}
