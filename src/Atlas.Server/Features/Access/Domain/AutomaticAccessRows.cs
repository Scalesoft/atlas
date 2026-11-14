using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>The locked rows of a resource's access list: everyone with access without a grant on the resource itself. WikiAdmin
/// manages everything; a space Admin (a current, valid Admin grant of the space, direct or through a current group) manages every
/// page (D4), vault and secret of the space.</summary>
public static class AutomaticAccessRows
{
    public static async Task<AutomaticAccess> WikiAdminsAsync(ISession session, string detail, CancellationToken ct, bool includeHub = true)
    {
        var count = await session.CreateSQLQuery("""
            SELECT count(*) FROM "UserAccount" u WHERE u."IsActive" AND
                (u."IsWikiAdmin" OR (:hub AND EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=u."Id")))
            """).SetParameter("hub", includeHub).UniqueResultAsync<long>(ct);
        return new AutomaticAccess("Správci wiki", detail, AccessLevels.Manage, checked((int)count));
    }

    /// <summary>Active space Admins who are not WikiAdmins (those are the row above).</summary>
    public static async Task<AutomaticAccess> SpaceAdminsAsync(ISession session, Guid spaceId, string detail, CancellationToken ct, bool includeHub = true)
    {
        var direct = QueryOver.Of<SpaceGrant>().Where(g => g.SpaceId == spaceId && g.Role == (int)SpaceRole.Admin && g.ActiveTo == null)
            .And(GrantQueryLimits.CurrentlyValid()).Select(g => g.UserId);
        var groups = QueryOver.Of<SpaceGrant>().Where(g => g.SpaceId == spaceId && g.Role == (int)SpaceRole.Admin && g.ActiveTo == null)
            .And(GrantQueryLimits.CurrentlyValid()).Select(g => g.GroupId);
        var members = QueryOver.Of<GroupMembership>().Where(m => m.ActiveTo == null)
            .And(Subqueries.PropertyIn("GroupId", groups.DetachedCriteria)).Select(m => m.UserId);
        var count = await session.QueryOver<UserAccount>().Where(u => u.IsActive && !u.IsWikiAdmin)
            .And(Expression.Sql("(? = false OR NOT EXISTS (SELECT 1 FROM \"EffectiveWikiAdmin\" h WHERE h.\"UserId\"={alias}.\"Id\"))", includeHub, NHibernate.NHibernateUtil.Boolean))
            .And(Restrictions.Or(Subqueries.PropertyIn("Id", direct.DetachedCriteria), Subqueries.PropertyIn("Id", members.DetachedCriteria)))
            .RowCountAsync(ct);
        return new AutomaticAccess("Správci prostoru", detail, AccessLevels.Manage, count);
    }
}
