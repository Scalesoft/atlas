using Atlas.Server.Features.Spaces.Domain;
using NHibernate;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>Shared runtime resolver for the effective space roles of an explicit account. IContentAccess, AdministrationAccess and collaboration use it and add their own status codes and messages.</summary>
public sealed class SpaceRoles
{
    private readonly ISession m_session;
    private readonly Dictionary<Guid, bool> m_inAi = [];

    public SpaceRoles(ISession session)
    {
        m_session = session;
    }

    /// <summary>One space, null while it is in the trash (the same rule as the map below), so no caller can forget it.</summary>
    public async Task<SpaceRole?> EffectiveAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
    {
        if (!actor.IsActive) return null;
        var best = await CurrentGrants(m_session, actor.Id)
            .And(g => g.SpaceId == spaceId)
            .Select(Projections.Max<SpaceGrant>(g => g.Role))
            .SingleOrDefaultAsync<object>(ct);
        return best is null ? null : (SpaceRole)Convert.ToInt32(best);
    }

    /// <summary>Every space outside the trash: a trashed space grants nothing (its grants are kept for the restore), so no caller
    /// of the map counts it, not even for rights beyond the space such as writing wiki objects.</summary>
    public async Task<IReadOnlyDictionary<Guid, SpaceRole>> EffectiveAsync(UserAccount actor, CancellationToken ct)
    {
        if (!actor.IsActive) return new Dictionary<Guid, SpaceRole>();
        var rows = await CurrentGrants(m_session, actor.Id)
            .SelectList(list => list.SelectGroup(g => g.SpaceId).SelectMax(g => g.Role))
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (SpaceRole)Convert.ToInt32(row[1]));
    }

    /// <summary>Current Admin authority, with the latest expiry among whole qualifying grants. Null expiry means unbounded only when Allowed.</summary>
    public Task<(bool Allowed, DateTime? ExpiresAt)> AdminAuthorityAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
        => AuthorityAsync(actor, spaceId, SpaceRole.Admin, ct);

    public async Task<(bool Allowed, DateTime? ExpiresAt)> AuthorityAsync(UserAccount actor, Guid spaceId, SpaceRole required, CancellationToken ct)
    {
        if (!actor.IsActive) return (false, null);
        var wiki = (await EffectiveWikiAdmin.ReadAsync(m_session, actor, ct)).Authority(DateTime.UtcNow);
        if (wiki.Allowed && wiki.ExpiresAt is null) return wiki;
        var groups = await GrantQueryLimits.GroupDeadlinesAsync(m_session, actor.Id, ct);
        var grants = await CurrentGrants(m_session, actor.Id).And(g => g.SpaceId == spaceId && g.Role >= (int)required).ListAsync(ct);
        var ends = grants.Select(g => GrantQueryLimits.Deadline(g.GroupId, g.ExpiresAt, groups)).ToList();
        if (wiki.Allowed) ends.Add(wiki.ExpiresAt);
        var now = DateTime.UtcNow;
        ends = ends.Where(end => end is null || now < end).ToList();
        return ends.Count == 0 ? (false, null) : (true, ends.Any(end => end is null) ? null : ends.Max());
    }

    public Task<bool> IsWikiAdminAsync(UserAccount actor, CancellationToken ct) => EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct);

    /// <summary>G5: whether the space takes part in AI and MCP (<see cref="AccessSql.SpaceInAi"/>), in the caller's transaction; read
    /// once per space and request, as every gate of an AI-bound request asks it again (PageGateLockTraceTests).</summary>
    public async Task<bool> InAiAsync(Guid spaceId, CancellationToken ct)
    {
        if (!m_inAi.TryGetValue(spaceId, out var included))
        {
            m_inAi[spaceId] = included = await m_session.CreateSQLQuery($"SELECT {AccessSql.SpaceInAi(":space")} AS included")
                .AddScalar("included", NHibernateUtil.Boolean).SetParameter("space", spaceId).UniqueResultAsync<bool>(ct);
        }
        return included;
    }

    public const string WikiAdminOnly = "Tuto operaci může provést pouze správce wiki.";

    /// <summary>The wiki-administrator gate: the caller locks or loads <paramref name="actor"/> first, this decides with
    /// <see cref="EffectiveWikiAdmin"/> (Hub administrators included) and answers 403 with <paramref name="refusal"/>.</summary>
    public async Task<UserAccount> RequireWikiAdminAsync(UserAccount actor, CancellationToken ct, string refusal = WikiAdminOnly)
    {
        if (!await IsWikiAdminAsync(actor, ct))
        {
            throw new ApiException(403, refusal);
        }
        return actor;
    }

    /// <summary>The account's current SpaceGrant rows, the one place of the rule that a space in the trash grants no role
    /// (its grants are kept for the restore); every reader of space grants for a decision starts here.</summary>
    public static IQueryOver<SpaceGrant, SpaceGrant> CurrentGrants(ISession session, Guid accountId)
    {
        return session.QueryOver<SpaceGrant>()
            .Where(g => g.ActiveTo == null)
            .And(GrantQueryLimits.CurrentlyValid())
            .And(GrantQueryLimits.OfAccount(accountId))
            .WithSubquery.WhereProperty(g => g.SpaceId).In(LiveSpace.Ids());
    }
}
