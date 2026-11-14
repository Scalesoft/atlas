using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>Visibility of pages and spaces in lists (tree, space pages, spaces, API lists, export selection), decided per page
/// by EffectivePageAccess (WP3 S3). A page is listed when the account may read it and it is published, or when the account
/// may edit it and owns the unpublished page (or is WikiAdmin), the 1.0 draft rule. Trash is filtered by the caller.</summary>
/// <remarks>D8: a space is listed for an account with a space role, or with at least one listed page in it; an account that
/// reaches only single pages sees the space's name, icon and colour, never its description or other pages.</remarks>
public sealed class PageListing
{
    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;

    public PageListing(ISession session, SpaceRoles roles)
    {
        m_session = session;
        m_roles = roles;
    }

    /// <summary>A space in a list context (tree, space pages, export selection) for an account locked or re-read by the caller:
    /// the space role, or null when the account reaches only single pages of the space. 404 when it reaches neither, as
    /// IContentAccess.RequireSpaceAsync answers.</summary>
    public async Task<SpaceRole?> RequireSpaceAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
    {
        if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct))
        {
            throw new ApiException(404, "Prostor nebyl nalezen.");
        }
        var role = await m_roles.IsWikiAdminAsync(actor, ct) ? SpaceRole.Admin : await m_roles.EffectiveAsync(actor, spaceId, ct);
        if (role is null && !await HasListedPageAsync(actor, spaceId, ct))
        {
            throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        return role;
    }

    /// <summary>The list draft rule over the page's published flag, owner and the account's document role on it.</summary>
    public static ICriterion Listed(IProjection isPublished, IProjection ownerId, IProjection role, UserAccount actor)
    {
        ICriterion editsDraft = Restrictions.Ge(role, 2);
        editsDraft = Restrictions.And(editsDraft, Restrictions.Or(Restrictions.Eq(ownerId, actor.Id),
            Expression.Sql("EXISTS (SELECT 1 FROM \"UserAccount\" ua WHERE ua.\"Id\"=? AND ua.\"IsActive\" AND (ua.\"IsWikiAdmin\" OR EXISTS (SELECT 1 FROM \"EffectiveWikiAdmin\" ha WHERE ha.\"UserId\"=ua.\"Id\")))", actor.Id, NHibernate.NHibernateUtil.Guid)));
        return Restrictions.And(Restrictions.Ge(role, 1), Restrictions.Or(Restrictions.Eq(isPublished, true), editsDraft));
    }

    public async Task<bool> HasListedPageAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
    {
        var any = await ListedPages(actor).And(x => x.SpaceId == spaceId).Select(x => x.PageId).Take(1).SingleOrDefaultAsync<Guid>(ct);
        return any != Guid.Empty;
    }

    /// <summary>Spaces in which the account has at least one listed page.</summary>
    public async Task<IList<Guid>> SpacesWithListedPagesAsync(UserAccount actor, CancellationToken ct)
    {
        return await ListedPages(actor)
            .Select(Projections.Distinct(Projections.Property<PageAccessEntry>(x => x.SpaceId)))
            .ListAsync<Guid>(ct);
    }

    private NHibernate.IQueryOver<PageAccessEntry, PageAccessEntry> ListedPages(UserAccount actor)
    {
        var actorId = actor.Id;
        return m_session.QueryOver<PageAccessEntry>()
            .Where(x => x.UserId == actorId && !x.IsTrashed)
            .And(Listed(Projections.Property<PageAccessEntry>(x => x.IsPublished), Projections.Property<PageAccessEntry>(x => x.OwnerId),
                Projections.Property<PageAccessEntry>(x => x.Role), actor));
    }
}
