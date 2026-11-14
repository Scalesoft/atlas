using Atlas.Server.Features.Spaces.Domain;
using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration;

/// <summary>
/// Spaces as an access kind: SpaceGrant read and written as the levels read, write and manage (Reader, Writer, Admin). A person's
/// level is the SpaceRoles decision (WikiAdmin: Admin of every space), a group's the highest valid grant of its own. The resource
/// list is for the space Admin (AdministrationAccess, as GET /api/spaces/{id}/access); grants go through SetSpaceGrantHandler.
/// </summary>
public sealed class SpaceAccessKind : IAccessKind, IAccessGrantWriter
{
    private const int EntryLimit = 200;

    private static readonly AccessKindDescriptor Kind = new("space", "Prostor", "Prostory", "book-open", null, null,
    [
        new(AccessLevels.Read, "Čte publikované stránky prostoru"),
        new(AccessLevels.Write, "Navíc píše a upravuje stránky"),
        new(AccessLevels.Manage, "Navíc spravuje přístupy, sdílení a trezory prostoru")
    ]);

    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly SpaceRoles m_roles;
    private readonly AdministrationAccess m_access;
    private readonly SetSpaceGrantHandler m_grants;

    public SpaceAccessKind(ISession session, IContentAccess content, SpaceRoles roles, AdministrationAccess access, SetSpaceGrantHandler grants)
    {
        m_session = session;
        m_content = content;
        m_roles = roles;
        m_access = access;
        m_grants = grants;
    }

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter Writer => this;

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        var account = who.Account;
        var (rows, valid) = await AccessGrantRows.SpaceAsync(m_session, Scoped(AccessGrantRows.Of(who), scope), ct);
        var wikiSources = account is null ? null : await EffectiveWikiAdmin.ReadAsync(m_session, account, ct);
        var wikiAdmin = wikiSources?.Authority(DateTime.UtcNow).Allowed == true;
        IReadOnlyDictionary<Guid, Space> spaces;
        if (wikiAdmin)
        {
            var all = LiveSpace.Query(m_session);
            if (scope.SpaceId is { } only)
            {
                all = all.Where(s => s.Id == only);
            }
            spaces = (await all.Take(AccessGrantRows.Limit).ListAsync(ct)).ToDictionary(s => s.Id);
        }
        else
        {
            spaces = await AccessGrantRows.SpacesAsync(m_session, rows.Select(r => r.SpaceId), ct);
        }
        var groups = await AccessGrantRows.GroupNamesAsync(m_session, rows.Select(r => r.GroupId), ct);
        var deadlines = account is null ? null : await GrantQueryLimits.GroupDeadlinesAsync(m_session, account.Id, ct);
        // A person's level is the SpaceRoles decision every space check uses; an inactive account has none.
        var roles = account is null ? null : await m_roles.EffectiveAsync(account, ct);
        var inherited = await InheritingPagesAsync(spaces.Keys.ToArray(), ct);
        return spaces.Values.OrderBy(s => s.Name, StringComparer.CurrentCulture).ThenBy(s => s.Id).Select(space =>
        {
            var sources = rows.Where(r => r.SpaceId == space.Id).Select(r => AccessSources.Grant(r.Id, r.GroupId,
                r.GroupId is { } g ? groups.GetValueOrDefault(g) : null, AccessLevels.FromRole(r.Role)!, r.ActiveFrom, r.StartAt,
                deadlines is null ? r.ExpiresAt : GrantQueryLimits.Deadline(r.GroupId, r.ExpiresAt, deadlines),
                valid.Contains(r.Id), who.IsUser && r.UserId is null)).ToList();
            if (wikiAdmin)
            {
                sources.Add(new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Manage, ExpiresAt: wikiSources!.Authority(DateTime.UtcNow).ExpiresAt, Effective: account!.IsActive,
                    VerificationLimited: wikiSources.Authority(DateTime.UtcNow).ExpiresAt is not null));
            }
            var level = account is null ? AccessSources.Level(sources)
                : account.IsActive && wikiAdmin ? AccessLevels.Manage
                : roles!.TryGetValue(space.Id, out var role) ? AccessLevels.FromRole((int)role) : null;
            var pages = inherited.GetValueOrDefault(space.Id);
            return new AccessEntry(Kind.Id, space.Id.ToString(), space.Name, null, space.Id, space.Name, level, AccessSources.Ordered(sources),
                pages == 0 ? null : AccessNotes.Count(pages, "stránka přebírá přístup prostoru", "stránky přebírají přístup prostoru", "stránek přebírá přístup prostoru"));
        }).ToArray();
    }

    public async Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct)
    {
        var term = AccessQuery.Term(query);
        var actor = await m_access.RequireSpaceAdminAsync(resourceId, false, ct);
        var space = (await m_session.GetAsync<Space>(resourceId, ct))!;
        var (rows, valid) = await AccessGrantRows.SpaceAsync(m_session, () => Restrictions.Eq("SpaceId", resourceId), ct);
        var groupIds = rows.Where(r => r.GroupId is not null).Select(r => r.GroupId!.Value).Distinct().ToArray();
        var groups = await AccessGrantRows.GroupNamesAsync(m_session, groupIds.Cast<Guid?>(), ct);
        var groupGrants = rows.Where(r => r.GroupId is not null).ToLookup(r => r.GroupId!.Value);
        var directGrants = rows.Where(r => r.UserId is not null).ToLookup(r => r.UserId!.Value);
        var effective = view == AccessView.Effective;
        var search = AdministrationReads.Search(term);
        // The members of the granted groups stay a subquery and come back as narrow rows: a group of every account is never an id list.
        ICriterion InGrantedGroups() => GrantQueryLimits.InChunks(Projections.Property<GroupMembership>(m => m.GroupId), groupIds);
        AccessEntrySource Source(SpaceGrant row, bool viaGroup) => AccessSources.Grant(row.Id, row.GroupId,
            row.GroupId is { } g ? groups.GetValueOrDefault(g) : null, AccessLevels.FromRole(row.Role)!, row.ActiveFrom, row.StartAt, row.ExpiresAt,
            valid.Contains(row.Id), viaGroup);

        var counts = groupIds.Length == 0 ? new Dictionary<Guid, int>() : (await m_session.QueryOver<GroupMembership>()
            .Where(m => m.ActiveTo == null).And(InGrantedGroups())
            .And(Subqueries.PropertyIn("UserId", QueryOver.Of<UserAccount>().Where(u => u.IsActive).Select(u => u.Id).DetachedCriteria))
            .SelectList(list => list.SelectGroup(m => m.GroupId).SelectCount(m => m.Id))
            .ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1]));
        var groupEntries = groupIds.Where(id => term.Length == 0 || groups.GetValueOrDefault(id)?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true).Select(id =>
        {
            var sources = AccessSources.Ordered(groupGrants[id].Select(r => Source(r, false)));
            var count = counts.GetValueOrDefault(id);
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.Group, id, groups.GetValueOrDefault(id) ?? "",
                AccessNotes.Count(count, "člen", "členové", "členů"), count), AccessSources.Level(sources), sources);
        }).OrderBy(e => e.Subject.Name, StringComparer.CurrentCulture).ToArray();

        // Direct: the people with a grant here. Effective: also every active member of a group with a grant here, as SpaceRoles resolves them.
        var directIds = directGrants.Select(g => g.Key).ToArray();
        var reach = Restrictions.Disjunction();
        if (directIds.Length > 0)
        {
            reach.Add(GrantQueryLimits.InChunks(Projections.Property<UserAccount>(u => u.Id), directIds));
        }
        if (effective && groupIds.Length > 0)
        {
            reach.Add(Restrictions.And(Restrictions.Eq(Projections.Property<UserAccount>(u => u.IsActive), true), Subqueries.PropertyIn("Id",
                QueryOver.Of<GroupMembership>().Where(m => m.ActiveTo == null).And(InGrantedGroups()).Select(m => m.UserId).DetachedCriteria)));
        }
        IList<object[]> people = [];
        if (directIds.Length > 0 || (effective && groupIds.Length > 0))
        {
            var users = m_session.QueryOver<UserAccount>().Where(reach);
            people = await (search is null ? users : users.And(search))
                .Select(u => u.Id, u => u.DisplayName, u => u.Username, u => u.IsActive, u => u.IsWikiAdmin).ListAsync<object[]>(ct);
        }
        IList<object[]> memberships = [];
        var hubAdmins = new Dictionary<Guid, DateTime>();
        if (effective && people.Count > 0)
        {
            if (groupIds.Length > 0)
            {
                var own = m_session.QueryOver<GroupMembership>().Where(m => m.ActiveTo == null).And(InGrantedGroups());
                memberships = await (search is null ? own : own.And(Subqueries.PropertyIn("UserId",
                        QueryOver.Of<UserAccount>().Where(search).Select(u => u.Id).DetachedCriteria)))
                    .Select(m => m.UserId, m => m.GroupId, m => m.VerifiedUntil).ListAsync<object[]>(ct);
            }
            // The Hub's WikiAdmins are few: the whole view, not a lookup per person.
            hubAdmins = (await m_session.CreateSQLQuery("SELECT \"UserId\", \"HubVerifiedUntil\" FROM \"EffectiveWikiAdmin\"")
                .ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => (DateTime)row[1]);
        }
        var groupsOf = memberships.ToLookup(row => (Guid)row[0]);
        var personEntries = people.Select(row =>
        {
            var (id, isActive) = ((Guid)row[0], (bool)row[3]);
            var sources = directGrants[id].Select(r => Source(r, false)).ToList();
            if (effective)
            {
                var own = groupsOf[id].ToDictionary(m => (Guid)m[1], m => (DateTime?)m[2]);
                sources.AddRange(own.Keys.SelectMany(g => groupGrants[g])
                    .Select(r => Source(r, true) with { ExpiresAt = GrantQueryLimits.Deadline(r.GroupId, r.ExpiresAt, own) }));
                if (hubAdmins.TryGetValue(id, out var hubUntil))
                {
                    sources.Add(new AccessEntrySource(AccessSources.Automatic, "Správce wiki ze skupiny Hubu", AccessLevels.Manage, ExpiresAt: hubUntil, VerificationLimited: true));
                }
                if ((bool)row[4])
                {
                    sources.Add(new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Manage));
                }
            }
            var ordered = AccessSources.Ordered(sources);
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.User, id, (string)row[1], (string)row[2], null, isActive),
                isActive ? AccessSources.Level(ordered) : null, ordered);
        }).OrderByDescending(e => AccessLevels.Rank(e.Level)).ThenBy(e => e.Subject.Name, StringComparer.CurrentCulture).ThenBy(e => e.Subject.Id).ToArray();
        var entries = groupEntries.Concat(personEntries).ToArray();
        var automatic = new[] { await AutomaticAccessRows.WikiAdminsAsync(m_session, "Mají přístup ke všem prostorům", ct) };
        return new ResourceAccess(new AccessResource(Kind.Id, space.Id.ToString(), space.Name, null, space.Id, space.Name), true, automatic,
            entries.Take(EntryLimit).ToArray(), entries.Length > EntryLimit, await SpaceAccessExceptions.ReadAsync(m_session, actor, space.Id, ct));
    }

    public async Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct)
    {
        var term = AccessQuery.Term(query);
        var actor = await m_content.LockActorAsync(null, ct);
        var spaces = LiveSpace.Query(m_session);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            var administered = (await m_roles.EffectiveAsync(actor, ct)).Where(r => r.Value == SpaceRole.Admin).Select(r => r.Key).ToArray();
            if (administered.Length == 0)
            {
                return [];
            }
            spaces = spaces.Where(GrantQueryLimits.InChunks(Projections.Property<Space>(s => s.Id), administered));
        }
        if (term.Length > 0)
        {
            spaces = spaces.And(AccessQuery.Contains("{alias}.\"Name\"", term));
        }
        var found = await spaces.OrderBy(s => s.Name).Asc.ThenBy(s => s.Id).Asc.Take(AccessQuery.Limit).ListAsync(ct);
        return found.Select(s => new AccessResource(Kind.Id, s.Id.ToString(), s.Name, null, s.Id, s.Name)).ToArray();
    }

    public Task ApplyAsync(Guid resourceId, AccessGrantChange change, CancellationToken ct)
    {
        var role = change.Level is null ? null : AccessLevels.RoleName(change.Level)
            ?? throw new ApiException(400, "Vyberte úroveň přístupu k prostoru: read (čte), write (upravuje) nebo manage (spravuje).");
        return m_grants.Handle(new SetSpaceGrant(resourceId, new UpdateSpaceGrant(change.UserId, change.GroupId, role, change.ExpectedVersion,
            change.StartAt, change.ExpiresAt)), ct);
    }

    private static Func<ICriterion> Scoped(Func<ICriterion> subject, AccessScope scope)
    {
        return scope.SpaceId is { } spaceId ? () => Restrictions.And(subject(), Restrictions.Eq("SpaceId", spaceId)) : subject;
    }

    /// <summary>Pages that follow the space defaults (no own access; not trashed, not templates), per space, in one grouped query.</summary>
    private async Task<IReadOnlyDictionary<Guid, int>> InheritingPagesAsync(Guid[] spaceIds, CancellationToken ct)
    {
        if (spaceIds.Length == 0)
        {
            return new Dictionary<Guid, int>();
        }
        var rows = await m_session.QueryOver<Page>()
            .Where(p => !p.OwnAccess && !p.IsTrashed && !p.IsTemplate)
            .And(GrantQueryLimits.InChunks(Projections.Property<Page>(p => p.SpaceId), spaceIds))
            .SelectList(list => list.SelectGroup(p => p.SpaceId).SelectCount(p => p.Id))
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1]));
    }
}
