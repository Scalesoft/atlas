using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Closes external share links (PageShareLink) in the transaction of the change that ends their authority, and audits each closure as
/// share.revoked with the actor of that change. A link lives while its issuer holds Share on the page for its whole life, as
/// ExternalSharingAccess.CanShareUntilAsync decides on every open: closing it here is the mutation-time half. Never reopens a link;
/// regranting access, untrashing or moving back cannot revive the old token. Database triggers did this until 3.6 (migrations
/// 2026091301 and 2026092622, dropped by 2026110941); ShareRevocationCoverageTests keeps every writer of the rights calling here.
/// </summary>
public static class ShareRevocation
{
    /// <summary>The issuer still holds Share on the link's page until the link expires: WikiAdmin (local or by a verified Hub group), a space
    /// Admin grant or a page DocumentManager grant, own or through a current membership, covering the expiry. Was
    /// atlas_share_creator_can_share_page_until.</summary>
    private const string IssuerKeepsShare = """
        EXISTS (SELECT 1 FROM "UserAccount" u WHERE u."Id" = l."CreatedById" AND u."IsActive" AND (u."IsWikiAdmin"
            OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId" = l."CreatedById" AND l."ExpiresAt" <= h."HubVerifiedUntil")
            OR EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId" = l."SpaceId" AND g."Role" = 3
                AND (g."ExpiresAt" IS NULL OR l."ExpiresAt" <= g."ExpiresAt")
                AND (g."UserId" = l."CreatedById" OR EXISTS (SELECT 1 FROM "EffectiveGroupMember" m WHERE m."UserId" = l."CreatedById"
                    AND m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL AND (m."VerifiedUntil" IS NULL OR l."ExpiresAt" <= m."VerifiedUntil"))))
            OR EXISTS (SELECT 1 FROM "EffectiveResourceGrant" g WHERE g."PageId" = l."PageId" AND g."Role" = 3
                AND (g."ExpiresAt" IS NULL OR l."ExpiresAt" <= g."ExpiresAt")
                AND (g."UserId" = l."CreatedById" OR EXISTS (SELECT 1 FROM "EffectiveGroupMember" m WHERE m."UserId" = l."CreatedById"
                    AND m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL AND (m."VerifiedUntil" IS NULL OR l."ExpiresAt" <= m."VerifiedUntil"))))))
        """;

    /// <summary>Whether the closing rule above keeps a link of this issuer, page and expiry: for the parity test with
    /// ExternalSharingAccess.CanShareUntilAsync, which decides on every open.</summary>
    public static Task<bool> IssuerKeepsShareAsync(ISession session, Guid issuerId, Guid spaceId, Guid pageId, DateTime expiresAt, CancellationToken ct)
        => session.CreateSQLQuery($"""
            SELECT {IssuerKeepsShare}
            FROM (SELECT CAST(:issuer AS uuid) AS "CreatedById", CAST(:space AS uuid) AS "SpaceId", CAST(:page AS uuid) AS "PageId",
                CAST(:expiry AS timestamptz) AS "ExpiresAt") l
            """).SetParameter("issuer", issuerId).SetParameter("space", spaceId).SetParameter("page", pageId)
            .SetParameter("expiry", expiresAt, NHibernateUtil.UtcDateTime).UniqueResultAsync<bool>(ct);

    /// <summary>The links of these pages: a page moved to another space or trashed.</summary>
    public static Task PagesAsync(ISession session, IReadOnlyCollection<Guid> pageIds, Guid? actorId, CancellationToken ct)
        => pageIds.Count == 0 ? Task.CompletedTask : CloseAsync(session, $"""l."PageId" = {GuidList.Any(":ids")}""", GuidList.Param(pageIds), actorId, ct);

    /// <summary>One link whose issuer's authority ran out without a change that closes links (a grant's or a Hub verification's
    /// expiry): ExternalSharingAccess closes it at its first refused open.</summary>
    public static Task LinkAsync(ISession session, Guid linkId, Guid? actorId, CancellationToken ct)
        => CloseAsync(session, """l."Id" = CAST(:ids AS uuid)""", linkId.ToString(), actorId, ct);

    /// <summary>Every link of this issuer: its security stamp, activation or WikiAdmin changed.</summary>
    public static Task IssuerAsync(ISession session, Guid userId, Guid? actorId, CancellationToken ct)
        => CloseAsync(session, """l."CreatedById" = CAST(:ids AS uuid)""", userId.ToString(), actorId, ct);

    /// <summary>After closing space grants: the links in their spaces of their subjects (the user, or a current member of the group) whose
    /// issuer has no Share left. Only Admin grants (role 3) carry Share.</summary>
    public static Task SpaceGrantsClosedAsync(ISession session, IReadOnlyCollection<Guid> grantIds, Guid? actorId, CancellationToken ct)
        => grantIds.Count == 0 ? Task.CompletedTask : CloseAsync(session, $"""
            EXISTS (SELECT 1 FROM "SpaceGrant" g WHERE g."Id" = {GuidList.Any(":ids")} AND g."Role" = 3 AND g."SpaceId" = l."SpaceId"
                AND (g."UserId" = l."CreatedById" OR EXISTS (SELECT 1 FROM "GroupMember" m
                    WHERE m."GroupId" = g."GroupId" AND m."UserId" = l."CreatedById" AND m."ActiveTo" IS NULL)))
            AND NOT {IssuerKeepsShare}
            """, GuidList.Param(grantIds), actorId, ct);

    /// <summary>After closing page grants: the links of their pages issued by their subjects whose issuer has no Share left. Only
    /// DocumentManager grants (role 3) carry Share.</summary>
    public static Task ResourceGrantsClosedAsync(ISession session, IReadOnlyCollection<Guid> grantIds, Guid? actorId, CancellationToken ct)
        => grantIds.Count == 0 ? Task.CompletedTask : CloseAsync(session, $"""
            EXISTS (SELECT 1 FROM "ResourceGrant" g WHERE g."Id" = {GuidList.Any(":ids")} AND g."Role" = 3 AND g."PageId" = l."PageId"
                AND (g."UserId" = l."CreatedById" OR EXISTS (SELECT 1 FROM "GroupMember" m
                    WHERE m."GroupId" = g."GroupId" AND m."UserId" = l."CreatedById" AND m."ActiveTo" IS NULL)))
            AND NOT {IssuerKeepsShare}
            """, GuidList.Param(grantIds), actorId, ct);

    /// <summary>After closing memberships: the links of their members whose issuer has no Share left.</summary>
    public static Task MembershipsClosedAsync(ISession session, IReadOnlyCollection<Guid> memberIds, Guid? actorId, CancellationToken ct)
        => memberIds.Count == 0 ? Task.CompletedTask : CloseAsync(session, $"""
            l."CreatedById" IN (SELECT m."UserId" FROM "GroupMember" m WHERE m."Id" = {GuidList.Any(":ids")})
            AND NOT {IssuerKeepsShare}
            """, GuidList.Param(memberIds), actorId, ct);

    /// <summary>Every open link in a space whose external sharing an administrator turned off, closed at the policy's boundary.</summary>
    public static async Task SpaceAsync(ISession session, Guid spaceId, DateTime boundary, Guid? actorId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        await session.CreateSQLQuery(Revoke("""greatest(:now, l."ActiveFrom" + interval '1 microsecond')""", """l."SpaceId" = :space"""))
            .SetParameter("now", boundary, NHibernateUtil.UtcDateTime)
            .SetParameter("space", spaceId)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .ExecuteUpdateAsync(ct);
    }

    /// <summary>The audit of one link written through the session: share.created by its issuer (CreateShareLink), or share.revoked
    /// by its issuer or a manager (RevokeShareLink). The session flushes first. Was TR_PageShareLink_Audit.</summary>
    public static async Task RecordAsync(ISession session, string action, Guid linkId, Guid pageId, Guid revisionId, Guid? actorId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        await session.CreateSQLQuery("""
            INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","ShareLinkId","Action") VALUES (:page, :revision, :actor, :link, :action)
            """)
            .SetParameter("action", action is "share.created" or "share.revoked" ? action : throw new ArgumentException("Not a share action", nameof(action)))
            .SetParameter("page", pageId)
            .SetParameter("revision", revisionId)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("link", linkId)
            .ExecuteUpdateAsync(ct);
    }

    // Flushes first: the rights change of the caller may still be pending in the session, and the authority read must see it. A link
    // past its expiry is left as it is, as the triggers left it.
    private static async Task CloseAsync(ISession session, string where, string ids, Guid? actorId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        await session.CreateSQLQuery(Revoke("""greatest(clock_timestamp(), l."ActiveFrom" + interval '1 microsecond')""",
                $"""l."ExpiresAt" > clock_timestamp() AND ({where})"""))
            .SetParameter("ids", ids)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .ExecuteUpdateAsync(ct);
    }

    private static string Revoke(string activeTo, string where) => $"""
        WITH revoked AS (
            UPDATE "PageShareLink" l SET "ActiveTo" = {activeTo}
            WHERE l."ActiveTo" IS NULL AND {where}
            RETURNING l."Id", l."PageId", l."RevisionId")
        INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","ShareLinkId","Action")
        SELECT "PageId", "RevisionId", :actor, "Id", 'share.revoked' FROM revoked
        """;
}
