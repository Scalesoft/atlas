using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.PageOrganization.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess;

/// <summary>
/// Pages as an access kind: page grants (ResourceGrant on a page, roles DocumentReader, DocumentWriter, DocumentManager) as read,
/// write and manage. A card lists only the pages that differ from the space: those with own access in a space the principal reads
/// below Admin (level null when none of its sources applies; a space Admin is never excluded, D4) and those with a grant of the
/// principal. A person's level is its EffectivePageAccess row under the list draft rule, the decision lists and search make and
/// ResourceAccessPolicy agrees with. The resource list mirrors the access tab (PageAccessDirectory) for those with ManageAccess;
/// grants go through SetPageGrantHandler.
/// </summary>
public sealed class PageAccessKind : IAccessKind, IAccessGrantWriter
{
    private const int EntryLimit = 200;

    private static readonly AccessKindDescriptor Kind = new("page", "Stránka", "Stránky", "file-text", "space", null,
    [
        new(AccessLevels.Read, "Čte stránku"),
        new(AccessLevels.Write, "Navíc stránku upravuje"),
        new(AccessLevels.Manage, "Navíc spravuje přístup a odkazy stránky")
    ]);

    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly SpaceRoles m_roles;
    private readonly PageReader m_reader;
    private readonly SetPageGrantHandler m_grants;

    public PageAccessKind(ISession session, IContentAccess content, SpaceRoles roles, PageReader reader, SetPageGrantHandler grants)
    {
        m_session = session;
        m_content = content;
        m_roles = roles;
        m_reader = reader;
        m_grants = grants;
    }

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter Writer => this;

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        var account = who.Account;
        // A WikiAdmin manages every page; its wiki entry says so, and no page differs from that.
        if (account is not null && await EffectiveWikiAdmin.IsAdminAsync(m_session, account, ct))
        {
            return [];
        }
        var spaceRoles = await AccessGrantRows.SpaceRolesAsync(m_session, m_roles, who, ct);
        var (grants, valid) = await AccessGrantRows.ResourceAsync(m_session,
            () => Restrictions.And(AccessGrantRows.Of(who)(), Restrictions.IsNotNull("PageId")), ct);
        var ownSpaces = spaceRoles.Where(r => r.Value < SpaceRole.Admin && scope.Includes(r.Key)).Select(r => r.Key).ToArray();
        var own = ownSpaces.Length == 0 ? [] : await m_session.QueryOver<Page>()
            .Where(p => p.OwnAccess && !p.IsTrashed)
            .And(GrantQueryLimits.InChunks(Projections.Property<Page>(p => p.SpaceId), ownSpaces))
            .Select(p => p.Id).Take(AccessGrantRows.Limit).ListAsync<Guid>(ct);
        var ids = own.Concat(grants.Select(g => g.PageId!.Value)).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        var pages = (await m_session.QueryOver<Page>()
            .Where(p => !p.IsTrashed)
            .And(GrantQueryLimits.InChunks(Projections.Property<Page>(p => p.Id), ids))
            .ListAsync(ct)).Where(p => scope.Includes(p.SpaceId)).ToArray();
        var titles = await AccessPageTitles.EntriesAsync(m_session, pages.Select(p => p.Id).ToArray(), ct);
        var parents = await AccessPageTitles.EntriesAsync(m_session, pages.Select(p => p.ParentId).OfType<Guid>().ToArray(), ct);
        var spaces = await AccessGrantRows.SpacesAsync(m_session, pages.Select(p => p.SpaceId), ct);
        var groups = await AccessGrantRows.GroupNamesAsync(m_session, grants.Select(g => g.GroupId), ct);
        var deadlines = account is null ? null : await GrantQueryLimits.GroupDeadlinesAsync(m_session, account.Id, ct);
        var roles = account is null ? null : (await m_session.QueryOver<PageAccessEntry>()
            .Where(e => e.UserId == account.Id)
            .And(GrantQueryLimits.InChunks(Projections.Property<PageAccessEntry>(e => e.PageId), pages.Select(p => p.Id).ToArray()))
            .ListAsync(ct)).ToDictionary(e => e.PageId, e => e.Role);
        var viewerAdmin = await EffectiveWikiAdmin.IsAdminAsync(m_session, scope.Viewer, ct);
        var entries = new List<AccessEntry>();
        foreach (var page in pages)
        {
            if (!titles.TryGetValue(page.Id, out var title) || !AccessPageTitles.Visible(title, scope.Viewer, viewerAdmin) || !spaces.TryGetValue(page.SpaceId, out var space))
            {
                continue;
            }
            var sources = grants.Where(g => g.PageId == page.Id).Select(g => AccessSources.Grant(g.Id, g.GroupId,
                g.GroupId is { } id ? groups.GetValueOrDefault(id) : null, AccessLevels.FromRole(g.Role)!, g.ActiveFrom, g.StartAt,
                deadlines is null ? g.ExpiresAt : GrantQueryLimits.Deadline(g.GroupId, g.ExpiresAt, deadlines),
                valid.Contains(g.Id), who.IsUser && g.UserId is null)).ToList();
            var spaceRole = spaceRoles.TryGetValue(page.SpaceId, out var role) ? (int)role : 0;
            if (spaceRole == (int)SpaceRole.Admin)
            {
                sources.Add(new AccessEntrySource(AccessSources.Automatic, AccessSources.SpaceAdminLabel(space.Name), AccessLevels.Manage));
            }
            else if (spaceRole > 0 && !page.OwnAccess)
            {
                sources.Add(new AccessEntrySource(AccessSources.Derived, AccessSources.SpaceRoleLabel, AccessLevels.FromRole(spaceRole)!));
            }
            var ordered = AccessSources.Ordered(sources);
            var level = account is null ? GroupLevel(page, ordered)
                : roles!.TryGetValue(page.Id, out var pageRole) && Listed(page, pageRole, account) ? AccessLevels.FromRole(pageRole) : null;
            entries.Add(new AccessEntry(Kind.Id, page.Id.ToString(), AccessPageTitles.Title(title), AccessPageTitles.ParentTitle(title, parents, scope.Viewer, viewerAdmin),
                space.Id, space.Name, level, ordered, page.OwnAccess ? "Vlastní přístup — nepřebírá běžná práva prostoru" : null));
        }
        return entries.OrderBy(e => e.SpaceName, StringComparer.CurrentCulture).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct)
    {
        var page = await PageAccessGate.RequireAsync(m_reader, m_session, m_content, resourceId, AccessOperation.ManageAccess, ct);
        var viewer = await m_content.LockActorAsync(page.SpaceId, ct);
        var space = (await m_session.GetAsync<Space>(page.SpaceId, ct))!;
        var title = (await AccessPageTitles.EntriesAsync(m_session, [page.Id], ct))[page.Id];
        var parents = await AccessPageTitles.EntriesAsync(m_session, page.ParentId is { } parent ? [parent] : [], ct);
        var direct = view == AccessView.Direct;
        var groups = (await PageAccessDirectory.GroupsAsync(m_session, page, "", ct)).Select(group =>
        {
            var sources = AccessSources.Ordered(group.Sources.Where(s => !direct || s.Kind == "page").Select(s => Source(s, space.Name, true)));
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.Group, group.GroupId, group.Name,
                AccessNotes.Count(group.MemberCount, "člen", "členové", "členů"), group.MemberCount), GroupLevel(page, sources), sources);
        }).Where(e => e.Sources.Count > 0);
        var (people, hasMore) = await PageAccessDirectory.PeopleAsync(m_session, page, "", 0, EntryLimit, ct, granted: true);
        var persons = people.Select(person =>
        {
            var sources = AccessSources.Ordered(person.Sources.Where(s => !direct || (s.Kind == "page" && s.GroupId is null))
                .Select(s => Source(s, space.Name, false)));
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.User, person.UserId, person.DisplayName, person.Username, null, true),
                PersonLevel(page, person, sources), sources);
        }).Where(e => e.Sources.Count > 0);
        IReadOnlyList<AutomaticAccess> automatic =
        [
            await AutomaticAccessRows.WikiAdminsAsync(m_session, "Spravují všechny stránky", ct),
            await AutomaticAccessRows.SpaceAdminsAsync(m_session, space.Id, "Spravují všechny stránky prostoru", ct)
        ];
        return new ResourceAccess(new AccessResource(Kind.Id, page.Id.ToString(), AccessPageTitles.Title(title),
                AccessPageTitles.ParentTitle(title, parents, viewer, await EffectiveWikiAdmin.IsAdminAsync(m_session, viewer, ct)), space.Id, space.Name), true, automatic,
            groups.Concat(persons).ToArray(), hasMore);
    }

    public async Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct)
    {
        var term = AccessQuery.Term(query);
        var actor = await m_content.LockActorAsync(null, ct);
        var actorId = actor.Id;
        SpacePageEntry entry = null!;
        PageAccessEntry access = null!;
        var pages = m_session.QueryOver(() => entry).Where(() => !entry.IsTrashed);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            // ManageAccess is the DocumentManager role on the page (a space Admin everywhere in the space, D4, or a manager grant),
            // under the list draft rule, so another owner's draft is never offered.
            pages = pages.JoinEntityAlias(() => access, () => access.PageId == entry.Id && access.UserId == actorId)
                .And(() => access.Role == (int)AccessRole.DocumentManager)
                .And(PageListing.Listed(Projections.Property(() => entry.IsPublished), Projections.Property(() => entry.OwnerId),
                    Projections.Property(() => access.Role), actor));
        }
        if (term.Length > 0)
        {
            pages = pages.And(AccessQuery.Contains("coalesce({alias}.\"PublishedTitle\", {alias}.\"DraftTitle\")", term));
        }
        var found = await pages.OrderBy(() => entry.DraftTitle).Asc.ThenBy(() => entry.Id).Asc.Take(AccessQuery.Limit).ListAsync(ct);
        var spaces = await AccessGrantRows.SpacesAsync(m_session, found.Select(p => p.SpaceId), ct);
        return found.Where(p => spaces.ContainsKey(p.SpaceId)).Select(p => new AccessResource(Kind.Id, p.Id.ToString(), AccessPageTitles.Title(p),
            null, p.SpaceId, spaces[p.SpaceId].Name)).ToArray();
    }

    public Task ApplyAsync(Guid resourceId, AccessGrantChange change, CancellationToken ct)
    {
        var role = change.Level is null ? null : AccessLevels.RoleName(change.Level)
            ?? throw new ApiException(400, "Vyberte úroveň přístupu ke stránce: read (čte), write (upravuje) nebo manage (spravuje).");
        return m_grants.Handle(new SetPageGrant(resourceId, new UpdatePageGrant(change.UserId, change.GroupId, role, change.ExpectedVersion,
            change.StartAt, change.ExpiresAt)), ct);
    }

    /// <summary>The list draft rule (PageListing.Listed) for one account that is no WikiAdmin.</summary>
    private static bool Listed(Page page, int role, UserAccount account)
    {
        return role >= 1 && (page.PublishedRevisionId is not null || (role >= 2 && page.OwnerId == account.Id));
    }

    /// <summary>A group's level as EffectivePageAccess gives it to its members: a template is managed only through the space Admin
    /// role and read through everything else; an unpublished page is its owner's draft, which no group opens.</summary>
    private static string? GroupLevel(Page page, IReadOnlyList<AccessEntrySource> sources)
    {
        var level = AccessSources.Level(sources);
        if (level is null || page.PublishedRevisionId is null)
        {
            return null;
        }
        return page.IsTemplate && !sources.Any(s => s.Effective && s.Via == AccessSources.Automatic) ? AccessLevels.Read : level;
    }

    /// <summary>A person's level from the directory sources under the rules the checks add: the private draft rule and the template cap.</summary>
    private static string? PersonLevel(Page page, AccessPerson person, IReadOnlyList<AccessEntrySource> sources)
    {
        var level = AccessSources.Level(sources);
        if (level is null || !PageVisibility.IsVisible(page, person.UserId, person.IsWikiAdmin))
        {
            return null;
        }
        return page.IsTemplate && !sources.Any(s => s.Effective && s.Via == AccessSources.Automatic) ? AccessLevels.Read : level;
    }

    /// <summary>A directory source: global and spaceAdmin are automatic, a space default reaches the page from the space, a page
    /// grant is the subject's own (direct) or its group's.</summary>
    private static AccessEntrySource Source(AccessSource source, string spaceName, bool ofGroup)
    {
        var level = source.Role switch
        {
            "Admin" => AccessLevels.Manage,
            "Writer" => AccessLevels.Write,
            _ => AccessLevels.Read
        };
        return source.Kind switch
        {
            "hubGlobal" => new AccessEntrySource(AccessSources.Automatic, "Správce wiki ze skupiny Hubu", AccessLevels.Manage, ExpiresAt: source.ExpiresAt, VerificationLimited: true),
            "global" => new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Manage),
            "spaceAdmin" => new AccessEntrySource(AccessSources.Automatic, AccessSources.SpaceAdminLabel(spaceName), AccessLevels.Manage,
                source.GroupId, source.GrantId, null, source.StartAt, source.ExpiresAt, source.Effective),
            "space" => new AccessEntrySource(AccessSources.Derived,
                ofGroup || source.Name.Length == 0 ? AccessSources.SpaceRoleLabel : $"{AccessSources.SpaceRoleLabel} ({source.Name})", level,
                source.GroupId, source.GrantId, null, source.StartAt, source.ExpiresAt, source.Effective),
            _ => new AccessEntrySource(ofGroup || source.GroupId is null ? AccessSources.Direct : AccessSources.Group,
                ofGroup || source.GroupId is null ? AccessSources.DirectLabel : source.Name, level, source.GroupId, source.GrantId, source.Version,
                source.StartAt, source.ExpiresAt, source.Effective)
        };
    }
}
