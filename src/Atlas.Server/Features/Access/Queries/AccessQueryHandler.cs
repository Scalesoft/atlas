using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Access.Queries;

/// <summary>
/// The read side of the security centre (docs/access-model.md §3). Cards: a WikiAdmin sees any person or group across the wiki, a
/// space Admin only with the space it administers (owner decision: its card never leaves that space), everyone their own. The kinds
/// build the entries with their own evaluators; this handler only authorizes, picks the kinds and names the principal. The candidate
/// directory needs a search term and someone who manages something, and lists at most 20 active accounts or groups, never the whole
/// directory (access audit 2026-09-28).
/// </summary>
public sealed class AccessQueryHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly SpaceRoles m_roles;
    private readonly IEnumerable<IAccessKind> m_kinds;
    private IReadOnlyList<IAccessKind>? m_enabled;

    public AccessQueryHandler(ISession session, IContentAccess content, SpaceRoles roles, IEnumerable<IAccessKind> kinds)
    {
        m_session = session;
        m_content = content;
        m_roles = roles;
        m_kinds = kinds;
    }

    public async Task<IReadOnlyList<AccessKindView>> Handle(GetAccessKinds query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var views = new List<AccessKindView>();
        var wikiAdmin = await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct);
        foreach (var kind in await EnabledAsync(ct))
        {
            var descriptor = kind.Descriptor;
            // Raw IsWikiAdmin on purpose: Hub administrators get no vaults (VaultAccess decides the same way).
            var global = descriptor.Id is "vault" or "secret" ? actor.IsWikiAdmin : wikiAdmin;
            var grantable = kind.Writer is not null && (global || (await kind.SearchAsync(null, ct)).Count > 0);
            views.Add(new AccessKindView(descriptor.Id, descriptor.One, descriptor.Many, descriptor.Icon, descriptor.Parent, descriptor.Plugin,
                descriptor.Levels, grantable));
        }
        return views;
    }

    public async Task<PrincipalAccess> Handle(GetMyAccess query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var who = AccessPrincipal.Of(actor);
        return new PrincipalAccess(await PrincipalAsync(who, true, ct), "self", await EntriesAsync(who, new AccessScope(actor, null), ct), false);
    }

    public async Task<PrincipalAccess> Handle(GetPrincipalAccess query, CancellationToken ct)
    {
        var viewer = await m_content.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, viewer, ct) && (query.SpaceId is not { } spaceId || await m_roles.EffectiveAsync(viewer, spaceId, ct) != SpaceRole.Admin))
        {
            throw new ApiException(403, "Přístupy osoby nebo skupiny vidí správce wiki; správce prostoru jen ve svém prostoru.");
        }
        var who = query.Kind switch
        {
            AccessPrincipal.User => AccessPrincipal.Of(await m_session.GetAsync<UserAccount>(query.Id, ct)
                ?? throw new ApiException(404, "Uživatel nebyl nalezen.")),
            AccessPrincipal.Group => await m_session.GetAsync<UserGroup>(query.Id, ct) is not null
                ? new AccessPrincipal(AccessPrincipal.Group, query.Id, null)
                : throw new ApiException(404, "Skupina nebyla nalezena."),
            _ => throw new ApiException(404, "Uživatel ani skupina nebyli nalezeni.")
        };
        var scope = new AccessScope(viewer, query.SpaceId);
        return new PrincipalAccess(await PrincipalAsync(who, scope.SpaceId is null, ct), scope.SpaceId is null ? "wiki" : "space",
            await EntriesAsync(who, scope, ct), true);
    }

    public async Task<ResourceAccess> Handle(GetResourceAccess query, CancellationToken ct)
    {
        await m_content.LockActorAsync(null, ct);
        var view = query.View switch
        {
            null or "" or "effective" => AccessView.Effective,
            "direct" => AccessView.Direct,
            _ => throw new ApiException(400, "Zobrazení přístupů je effective nebo direct.")
        };
        var kind = await KindAsync(query.Kind, ct);
        if (!Guid.TryParse(query.Id, out var id))
        {
            throw new ApiException(404, "Položka nebyla nalezena.");
        }
        var resource = await kind.ForResourceAsync(id, view, query.Query, ct) ?? throw new ApiException(404, "U tohoto druhu se přístupy k jedné položce nevypisují.");
        var sources = await SourceDetailsAsync(resource.Entries.SelectMany(x => x.Sources), ct);
        return resource with { Entries = resource.Entries.Select(x => x with { Sources = x.Sources.Select(s => sources[s]).ToArray() }).ToArray() };
    }

    public async Task<IReadOnlyList<AccessResource>> Handle(SearchAccessResources query, CancellationToken ct)
    {
        await m_content.LockActorAsync(null, ct);
        return await (await KindAsync(query.Kind, ct)).SearchAsync(query.Query, ct);
    }

    public async Task<IReadOnlyList<AccessCandidate>> Handle(GetAccessCandidates query, CancellationToken ct)
    {
        if (query.Kind is not (AccessPrincipal.User or AccessPrincipal.Group))
        {
            throw new ApiException(400, "Vyberte uživatele nebo skupinu.");
        }
        var term = AccessQuery.Term(query.Query);
        if (term.Length == 0)
        {
            throw new ApiException(400, "Zadejte aspoň jeden znak jména.");
        }
        var actor = await m_content.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct) && !await ManagesSomethingAsync(ct))
        {
            throw new ApiException(403, "Lidi a skupiny může vyhledat jen ten, kdo spravuje přístupy.");
        }
        if (!query.ExceptMe)
        {
            return await PageAccessDirectory.CandidatesAsync(m_session, query.Kind, term, AccessQuery.Limit, ct);
        }
        var found = await PageAccessDirectory.CandidatesAsync(m_session, query.Kind, term, AccessQuery.Limit + 1, ct);
        return found.Where(candidate => candidate.Id != actor.Id).Take(AccessQuery.Limit).ToArray();
    }

    public async Task<RevokeAllImpact> Handle(GetRevokeAllImpact query, CancellationToken ct)
    {
        await m_roles.RequireWikiAdminAsync(await m_content.LockActorAsync(null, ct), ct);
        if (await m_session.GetAsync<UserAccount>(query.UserId, ct) is null)
        {
            throw new ApiException(404, "Uživatel nebyl nalezen.");
        }
        return await RevokeAllRows.ImpactAsync(m_session, query.UserId, ct);
    }

    /// <summary>The kinds offered now, asked once per message (a plugin kind reads its module configuration).</summary>
    private async Task<IReadOnlyList<IAccessKind>> EnabledAsync(CancellationToken ct)
    {
        if (m_enabled is null)
        {
            var enabled = new List<IAccessKind>();
            foreach (var kind in m_kinds)
            {
                if (await kind.IsEnabledAsync(ct))
                {
                    enabled.Add(kind);
                }
            }
            m_enabled = enabled;
        }
        return m_enabled;
    }

    private async Task<IAccessKind> KindAsync(string? id, CancellationToken ct)
    {
        return (await EnabledAsync(ct)).FirstOrDefault(k => k.Descriptor.Id == id) ?? throw new ApiException(404, "Druh přístupu nebyl nalezen.");
    }

    private async Task<IReadOnlyList<AccessEntry>> EntriesAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        var entries = new List<AccessEntry>();
        foreach (var kind in await EnabledAsync(ct))
        {
            entries.AddRange(await kind.ForPrincipalAsync(who, scope, ct));
        }
        var sources = await SourceDetailsAsync(entries.SelectMany(x => x.Sources), ct);
        var spaces = await AccessGrantRows.SpacesAsync(m_session, entries.Select(x => x.SpaceId).OfType<Guid>(), ct);
        return entries.Select(x => x with
        {
            Sources = x.Sources.Select(s => sources[s]).ToArray(),
            SpaceIcon = x.SpaceId is { } id ? spaces.GetValueOrDefault(id)?.Icon : null,
            SpaceColor = x.SpaceId is { } colorId ? spaces.GetValueOrDefault(colorId)?.Color : null
        }).ToArray();
    }

    private async Task<HashSet<Guid>> HubGroupsAsync(IEnumerable<AccessEntrySource> sources, CancellationToken ct)
    {
        var ids = sources.Where(x => x.GroupId.HasValue).Select(x => x.GroupId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var bound = await m_session.QueryOver<HubGroupBinding>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<HubGroupBinding>(x => x.GroupId), ids))
            .Select(x => x.GroupId).ListAsync<Guid>(ct);
        return bound.ToHashSet();
    }

    private async Task<Dictionary<AccessEntrySource, AccessEntrySource>> SourceDetailsAsync(IEnumerable<AccessEntrySource> sources, CancellationToken ct)
    {
        var rows = sources.Distinct().ToArray();
        var hubGroups = await HubGroupsAsync(rows, ct);
        var ids = rows.Where(x => x.GroupId is { } group && hubGroups.Contains(group) && x.GrantId.HasValue)
            .Select(x => x.GrantId!.Value).Distinct().ToArray();
        var ends = new Dictionary<Guid, DateTime?>();
        if (ids.Length > 0)
        {
            var spaceGrants = await m_session.QueryOver<SpaceGrant>()
                .Where(GrantQueryLimits.InChunks(Projections.Property<SpaceGrant>(x => x.Id), ids))
                .Select(x => x.Id, x => x.ExpiresAt).ListAsync<object[]>(ct);
            var resourceGrants = await m_session.QueryOver<ResourceGrant>()
                .Where(GrantQueryLimits.InChunks(Projections.Property<ResourceGrant>(x => x.Id), ids))
                .Select(x => x.Id, x => x.ExpiresAt).ListAsync<object[]>(ct);
            foreach (var grant in spaceGrants.Concat(resourceGrants)) ends[(Guid)grant[0]] = (DateTime?)grant[1];
        }
        // ExpiresAt remains the authorization deadline; only the display distinguishes a renewable Hub lease.
        return rows.ToDictionary(x => x, x => x with
        {
            GroupSource = x.GroupId is { } id ? hubGroups.Contains(id) ? "hub" : "local" : null,
            VerificationLimited = x.VerificationLimited || x.GrantId is { } grant && ends.TryGetValue(grant, out var end)
                && x.ExpiresAt is { } until && (end is null || until < end),
            GrantExpiresAt = x.GrantId is { } grantId && ends.TryGetValue(grantId, out var expiry) ? expiry : x.GrantExpiresAt
        });
    }

    /// <summary>Someone who may grant something of at least one kind.</summary>
    private async Task<bool> ManagesSomethingAsync(CancellationToken ct)
    {
        foreach (var kind in await EnabledAsync(ct))
        {
            if (kind.Writer is not null && (await kind.SearchAsync(null, ct)).Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The card's header. Sign-in time and group memberships are wiki-wide facts: a space Admin's card leaves them out.</summary>
    private async Task<AccessPrincipalView> PrincipalAsync(AccessPrincipal who, bool wide, CancellationToken ct)
    {
        if (who.Account is { } account)
        {
            DateTime? lastSignIn = null;
            IReadOnlyList<AccessGroupRef>? groups = null;
            if (wide)
            {
                lastSignIn = await m_session.QueryOver<UserSignIn>().Where(s => s.UserId == account.Id)
                    .Select(Projections.Max<UserSignIn>(s => s.AuthenticatedAt)).SingleOrDefaultAsync<DateTime?>(ct);
                var memberOf = await m_session.QueryOver<GroupMembership>().Where(m => m.UserId == account.Id && m.ActiveTo == null)
                    .Select(m => m.GroupId).ListAsync<Guid>(ct);
                groups = (await AccessGrantRows.GroupNamesAsync(m_session, memberOf.Cast<Guid?>(), ct))
                    .OrderBy(g => g.Value, StringComparer.CurrentCulture).Select(g => new AccessGroupRef(g.Key, g.Value)).ToArray();
            }
            return new AccessPrincipalView(AccessPrincipal.User, account.Id, account.DisplayName, account.Username, account.IsActive,
                await EffectiveWikiAdmin.IsAdminAsync(m_session, account, ct), lastSignIn is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, groups);
        }
        var group = (await m_session.GetAsync<UserGroup>(who.Id, ct))!;
        var activeMembers = QueryOver.Of<UserAccount>().Where(u => u.IsActive).Select(u => u.Id);
        var members = await m_session.QueryOver<GroupMembership>().Where(m => m.GroupId == who.Id && m.ActiveTo == null)
            .And(Subqueries.PropertyIn("UserId", activeMembers.DetachedCriteria))
            .RowCountAsync(ct);
        return new AccessPrincipalView(AccessPrincipal.Group, group.Id, group.Name, AccessNotes.Count(members, "člen", "členové", "členů"));
    }
}
