using NHibernate.Criterion;
using Atlas.Server.Features.PageOrganization.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentExport.Queries;

/// <summary>
/// Second, deliberately fresh transaction: it never reuses the content snapshot or the actor cached during
/// the build. Unlike BuildContentExportHandler, this handler goes through IContentAccess, so it holds the
/// shared advisory lock and the UserAccount/Space FOR SHARE locks that the build skipped — this is the
/// transaction that actually guarantees the archive still matches what the actor may see. Each
/// IMessageBus.InvokeAsync call also runs in its own DI scope and NHibernate session (pinned by
/// PlatformTests.Wolverine_handler_runs_in_its_own_scope_inside_a_transaction_and_rolls_back_on_failure: two
/// messages from one caller scope get two sessions), so this handler never sees the build's first-level cache.
/// </summary>
public sealed class RevalidateContentExportHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ResourceGrantSnapshots m_grants;
    private readonly PageListing m_listing;

    public RevalidateContentExportHandler(ISession session, IContentAccess access, ResourceGrantSnapshots grants, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_grants = grants;
        m_listing = listing;
    }

    public async Task<DownloadLease> Handle(RevalidateContentExport command, CancellationToken ct)
    {
        // spaceId: null is deliberate — only the advisory shared lock and the account FOR SHARE are taken here,
        // per the recorded deviation; the space and its pages are re-read (not locked) below.
        var current = await m_access.LockActorAsync(null, ct);
        var wikiAdmin = await EffectiveWikiAdmin.IsAdminAsync(m_session, current, ct);
        if (command.WholeSpace)
        {
            // Selecting a whole space is a list: a space role, or (D8) listed pages of the space.
            await m_listing.RequireSpaceAsync(current, command.SpaceId, ct);
        }
        var ids = command.Selected.Select(x => x.PageId).Distinct().ToArray();
        var pages = ids.Length == 0
            ? new Dictionary<Guid, Page>()
            : (await m_session.QueryOver<Page>().Where(GrantQueryLimits.InChunks(Projections.Property<Page>(x => x.Id), ids)).ListAsync(ct))
                .ToDictionary(x => x.Id);
        foreach (var selection in command.Selected)
        {
            // The private draft rule first, so an invisible page fails before any grant is loaded.
            if (!pages.TryGetValue(selection.PageId, out var page) || page.IsTrashed || page.SpaceId != command.SpaceId
                || selection.SpaceId != command.SpaceId || !PageVisibility.IsVisible(page, current.Id, wikiAdmin))
            {
                throw Missing();
            }
        }
        // ReadContent on every page, as IContentAccess decides it but without the space lock and with one grant query for all
        // pages; the final statement below re-evaluates each page together with the credential.
        var snapshot = await m_grants.ForPagesAsync(current, command.SpaceId, ids, ct);
        var now = DateTimeOffset.UtcNow;
        var lease = new DownloadLease(null);
        var working = new List<Guid>();
        foreach (var selection in command.Selected)
        {
            var page = pages[selection.PageId];
            var authorization = snapshot.Authorize(page, AccessOperation.ReadContent, now);
            var canEdit = authorization.CanEdit;
            if (!authorization.Allowed || (page.PublishedRevisionId is null && !canEdit))
            {
                throw Missing();
            }
            if (!canEdit && page.PublishedRevisionId != selection.RevisionId)
            {
                throw new ApiException(409, "Publikovaná verze nebo oprávnění se během exportu změnily. Spusťte export znovu.");
            }
            if (page.PublishedRevisionId != selection.RevisionId)
            {
                working.Add(page.Id);
            }
            var authority = snapshot.Authority(page, page.PublishedRevisionId == selection.RevisionId
                ? AccessOperation.ReadContent : AccessOperation.EditContent, now);
            lease = lease.Limit(authority.Until);
        }
        if (command.WholeSpace && ids.Length == 0)
        {
            var authority = await new SpaceRoles(m_session).AuthorityAsync(current, command.SpaceId, SpaceRole.Reader, ct);
            if (!authority.Allowed) throw Missing();
            lease = lease.Limit(authority.ExpiresAt);
        }
        lease = lease.Limit(await DownloadCredentials.ReadAsync(m_session, current, command.AuthenticatedTokenId, ct));
        await RequireFinalAuthorityAsync(command, current, working, ct);
        return lease;
    }

    private async Task RequireFinalAuthorityAsync(RevalidateContentExport command, UserAccount current, IReadOnlyCollection<Guid> working, CancellationToken ct)
    {
        // One final statement evaluates content and credential authority together after all page reads.
        // Separate awaited checks would allow either a grant or a token to expire during the other check.
        // Every selected page needs a current reader role on that page (EffectivePageAccess) while it is still in the exported
        // space, a working revision a writer role. A whole space also needs the space itself: a space role, or a selected page
        // (each of which is checked above).
        var authority = await m_session.CreateSQLQuery($"""
            WITH {AccessSql.ActorCte}
            SELECT
                (u."IsWikiAdmin" OR (
                    NOT EXISTS (
                        SELECT 1 FROM unnest(CAST(string_to_array(:pages, ',') AS uuid[])) AS s("PageId")
                        WHERE NOT EXISTS (
                            SELECT 1 FROM "EffectivePageAccess" a
                            WHERE a."UserId"=u."Id" AND a."PageId"=s."PageId" AND a."SpaceId"=:space
                              AND a."Role" >= CASE WHEN s."PageId" = ANY(CAST(string_to_array(:working, ',') AS uuid[])) THEN 2 ELSE 1 END))
                    AND (NOT :whole OR :pages <> '' OR EXISTS (
                        SELECT 1 FROM "EffectiveSpaceGrant" g
                        WHERE g."SpaceId"=:space
                          AND (g."UserId"=u."Id" OR EXISTS (
                               SELECT 1 FROM "EffectiveGroupMember" m
                              WHERE m."GroupId"=g."GroupId" AND m."UserId"=u."Id" AND m."ActiveTo" IS NULL))))
                )) AS "ContentAllowed",
                (:token IS NULL OR EXISTS (
                    SELECT 1 FROM "PersonalApiToken" t
                    WHERE t."Id"=:token AND t."UserId"=u."Id" AND t."SecurityStamp"=:stamp
                      AND t."ActiveTo" IS NULL AND t."ExpiresAt">statement_timestamp()
                      AND EXISTS (SELECT 1 FROM "PersonalApiTokenScope" s WHERE s."TokenId"=t."Id" AND s."Scope"='read')
                      AND EXISTS (SELECT 1 FROM "PersonalApiTokenChannel" c WHERE c."TokenId"=t."Id" AND c."Channel"='rest')
                      AND EXISTS (SELECT 1 FROM "UserApiPermission" p
                          WHERE p."UserId"=u."Id" AND p."ActiveTo" IS NULL AND p."Level"<>'none')
                )) AS "CredentialAllowed"
            FROM actor u
            """)
            .SetParameter("space", command.SpaceId)
            .SetParameter("whole", command.WholeSpace)
            .SetParameter("pages", string.Join(',', command.Selected.Select(x => x.PageId)))
            .SetParameter("working", string.Join(',', working))
            .SetParameter("token", command.AuthenticatedTokenId, NHibernate.NHibernateUtil.Guid)
            .SetParameter("actor", current.Id)
            .SetParameter("stamp", current.SecurityStamp)
            .UniqueResultAsync<object[]>(ct);
        if (authority is null || !(bool)authority[1])
        {
            throw new ApiException(401, "Platnost osobního API tokenu během exportu skončila.");
        }
        if (!(bool)authority[0])
        {
            throw Missing();
        }
    }

    private static ApiException Missing()
    {
        return new ApiException(404, "Obsah nebyl nalezen nebo k němu nemáte přístup.");
    }
}
