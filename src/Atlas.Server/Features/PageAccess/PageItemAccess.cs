using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess;

/// <summary>Item access carried along by page lifecycle writes (WP3 S4). Callers hold the page's space lock (FOR SHARE when
/// only the parent's grants are read, FOR UPDATE when grants are closed) as IContentAccess.RequirePageAsync requires, and pass the
/// locked actor, whom the audit names.</summary>
public static class PageItemAccess
{
    /// <summary>Copy-on-create (D2, D12): a new subpage starts with its parent's access mode and a copy of every current grant
    /// of the parent, with the same validity. Nobody gains a page they could not open before (the parent's audience is the
    /// child's), an own-access parent does not leak its child to the whole space, and the creator keeps the access that let
    /// it create the page, also when that access is only a grant on the parent. The page must already be flushed.</summary>
    public static async Task CopyFromParentAsync(ISession session, Guid pageId, Guid parentId, Guid actorId, CancellationToken ct)
    {
        var copied = await session.CreateSQLQuery("""
            INSERT INTO "ResourceGrant"("Id","PageId","UserId","GroupId","Role","StartAt","ExpiresAt")
            SELECT gen_random_uuid(),:page,"UserId","GroupId","Role","StartAt","ExpiresAt"
            FROM "ResourceGrant" WHERE "PageId"=:parent AND "ActiveTo" IS NULL
            RETURNING "Id"
            """).SetParameter("page", pageId).SetParameter("parent", parentId).ListAsync<Guid>(ct);
        await AccessAuditLog.OpenedAsync(session, AccessAuditLog.ResourceGrant, [.. copied], actorId, ct);
    }

    /// <summary>D7: pages leaving their space take the target space's default access. Resets the access mode of the given
    /// (locked) pages and closes their current grants; the history and audit of the closed grants stay.</summary>
    public static async Task ResetAsync(ISession session, IReadOnlyCollection<Page> pages, Guid actorId, CancellationToken ct)
    {
        foreach (var page in pages)
        {
            page.OwnAccess = false;
            await PageAccessModeHistory.RecordAsync(session, page, actorId, ct);
        }
        await session.FlushAsync(ct);
        foreach (var chunk in pages.Select(x => x.Id).Chunk(1000))
        {
            var closed = await session.CreateSQLQuery("""
                UPDATE "ResourceGrant" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
                WHERE "PageId" IN (:pages) AND "ActiveTo" IS NULL
                RETURNING "Id"
                """).SetParameterList("pages", chunk, NHibernateUtil.Guid).ListAsync<Guid>(ct);
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actorId, ct);
        }
    }
}
