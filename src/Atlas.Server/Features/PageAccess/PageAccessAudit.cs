using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess;

public static class PageAccessAudit
{
    // Call only after authorization and content lookup, inside the delivery handler's transaction.
    // Failure must propagate: do not return content when its audit cannot be persisted.
    // An actor's opening carries its channel (and, for api and mcp, the token); a share-link opening carries none.
    public static Task RecordOpeningAsync(ISession session, Guid pageId, Guid revisionId, Guid? actorId,
        Guid? shareLinkId, RequestChannel? channel, CancellationToken ct) => session.CreateSQLQuery("""
            INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","ShareLinkId","Action","Channel","ApiTokenId")
            VALUES (:page,:revision,:actor,:link,:action,:channel,:token)
            """).SetParameter("page", pageId).SetParameter("revision", revisionId)
        .SetParameter("actor", actorId, NHibernateUtil.Guid).SetParameter("link", shareLinkId, NHibernateUtil.Guid)
        .SetParameter("action", shareLinkId is null ? "page.opened" : "share.opened")
        .SetParameter("channel", channel?.Code, NHibernateUtil.String).SetParameter("token", channel?.ApiTokenId, NHibernateUtil.Guid)
        .ExecuteUpdateAsync(ct);
}
