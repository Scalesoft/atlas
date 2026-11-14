using NHibernate.Criterion;
using Atlas.Server.Features.PageOrganization.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>The trusted input of ResourceAccessPolicy for pages of one space: the account, the space defaults and the page grants
/// of the pages it was loaded for. A page it was not loaded for gets only the space defaults, so load it for every page it decides.</summary>
public sealed record ResourceGrantSnapshot(ResourceAccessPrincipal Principal, IReadOnlyList<ResourceAccessGrant> Grants)
{
    /// <summary>The longest independent path for this operation, including the private-draft condition. Null means unbounded only when allowed.</summary>
    public (bool Allowed, DateTime? Until) Authority(Page page, AccessOperation operation, DateTimeOffset now)
    {
        var authorization = Authorize(page, operation, now);
        if (!authorization.Allowed) return (false, null);
        if (authorization.Decision.LegacyAdministrator && Principal.IsWikiAdmin) return (true, null);
        var ends = new List<DateTime?>();
        if (authorization.Decision.LegacyAdministrator && Principal.HubWikiAdminUntil is { } hub) ends.Add(hub.UtcDateTime);
        var ordinary = this with { Principal = Principal with { HubWikiAdminUntil = null } };
        var decision = ordinary.Authorize(page, operation, now).Decision;
        ends.AddRange(Grants.Where(grant => decision.GrantIds.Contains(grant.Id)).Select(grant => grant.ExpiresAt?.UtcDateTime));
        return (true, ends.Any(end => end is null) ? null : ends.Max());
    }

    /// <summary>ResourceAccessPolicy alone, without the private draft rule; point-of-use paths decide through Authorize.</summary>
    public ResourceAccessDecision Evaluate(Page page, AccessOperation operation, DateTimeOffset now)
    {
        var target = new ResourceAccessTarget(page.SpaceId, AccessResourceKind.Document, page.Id, page.OwnAccess, page.IsTemplate);
        return ResourceAccessPolicy.Evaluate(Principal, target, operation, Grants, now);
    }

    /// <summary>The one page decision of every point-of-use path: the private draft rule (PageVisibility), then the policy.
    /// Only a page the account can read may reveal that it exists, so a denied operation on an unreadable page is Missing.</summary>
    public PageAuthorization Authorize(Page page, AccessOperation operation, DateTimeOffset now)
    {
        if (!PageVisibility.IsVisible(page, Principal.SubjectId, Principal.IsWikiAdminAt(now)))
        {
            return PageAuthorization.Missing(operation);
        }
        var decision = Evaluate(page, operation, now);
        var canEdit = operation == AccessOperation.EditContent ? decision.Allowed : Evaluate(page, AccessOperation.EditContent, now).Allowed;
        if (decision.Allowed)
        {
            return new PageAuthorization(PageAccessOutcome.Allowed, operation, decision, canEdit);
        }
        if (operation is AccessOperation.Discover or AccessOperation.ReadContent || !Evaluate(page, AccessOperation.ReadContent, now).Allowed)
        {
            return new PageAuthorization(PageAccessOutcome.Missing, operation, decision, false);
        }
        return new PageAuthorization(PageAccessOutcome.Forbidden, operation, decision, canEdit);
    }

    /// <summary>The same account and space defaults with page grants added.</summary>
    public ResourceGrantSnapshot With(IReadOnlyCollection<ResourceAccessGrant> pageGrants)
    {
        if (pageGrants.Count == 0)
        {
            return this;
        }
        var groups = Principal.GroupIds.Concat(GroupsOf(pageGrants)).ToHashSet();
        return new ResourceGrantSnapshot(Principal with { GroupIds = groups }, Grants.Concat(pageGrants).ToArray());
    }

    internal static IEnumerable<Guid> GroupsOf(IEnumerable<ResourceAccessGrant> grants)
    {
        return grants.Where(g => g.SubjectKind == AccessSubjectKind.Group).Select(g => g.SubjectId);
    }
}

/// <summary>Loads ResourceAccessPolicy snapshots for an explicit account, in the style of SpaceRoles.</summary>
/// <remarks>Only current assignments that are valid at the database statement clock are loaded, as in the EffectiveSpaceGrant,
/// EffectiveResourceGrant and EffectivePageAccess views; the evaluator applies the same bounds again. Group grants are matched in
/// SQL against every current group of the account (a subquery, no identifier list), and the principal carries the groups of the
/// loaded grants, which is all the policy compares. A WikiAdmin needs no grant for documents, so nothing is loaded for one.</remarks>
public sealed class ResourceGrantSnapshots
{
    private readonly ISession m_session;

    public ResourceGrantSnapshots(ISession session)
    {
        m_session = session;
    }

    /// <summary>The page decision for an explicit account on a path without IContentAccess (collaboration relay, export build,
    /// share link issuer). An invisible page is decided by the private draft rule before any grant is loaded.</summary>
    public async Task<PageAuthorization> AuthorizePageAsync(UserAccount actor, Page page, AccessOperation operation, CancellationToken ct)
    {
        var sources = await EffectiveWikiAdmin.ReadAsync(m_session, actor, ct);
        if (!PageVisibility.IsVisible(page, actor.Id, sources.Authority(DateTime.UtcNow).Allowed))
        {
            return PageAuthorization.Missing(operation);
        }
        return (await ForSpaceAsync(actor, page.SpaceId, ct, sources)).With(await PageGrantsAsync(actor, page.SpaceId, [page.Id], ct))
            .Authorize(page, operation, DateTimeOffset.UtcNow);
    }

    /// <summary>One page: SpaceGrant rows of its space become document space defaults, ResourceGrant rows on it exact page grants.</summary>
    public Task<ResourceGrantSnapshot> ForPageAsync(UserAccount actor, Page page, CancellationToken ct)
    {
        return ForPagesAsync(actor, page.SpaceId, [page.Id], ct);
    }

    /// <summary>Pages of one space in two queries: the space defaults and one ResourceGrant query for all the pages.</summary>
    public async Task<ResourceGrantSnapshot> ForPagesAsync(UserAccount actor, Guid spaceId, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        var space = await ForSpaceAsync(actor, spaceId, ct);
        return space.With(await PageGrantsAsync(actor, spaceId, pageIds, ct));
    }

    /// <summary>The account and the space defaults only; add PageGrantsAsync of the pages before deciding them.</summary>
    /// <param name="sources">The account's EffectiveWikiAdmin, when the caller read it in this transaction under the account's lock.</param>
    public async Task<ResourceGrantSnapshot> ForSpaceAsync(UserAccount actor, Guid spaceId, CancellationToken ct, WikiAdminSources? sources = null)
    {
        sources ??= await EffectiveWikiAdmin.ReadAsync(m_session, actor, ct);
        if (!actor.IsActive || sources.Persisted)
        {
            return new ResourceGrantSnapshot(Principal(actor, sources, []), []);
        }
        var rows = await SpaceRoles.CurrentGrants(m_session, actor.Id).And(g => g.SpaceId == spaceId).ListAsync(ct);
        var groups = rows.Any(g => g.GroupId.HasValue)
            ? await GrantQueryLimits.GroupDeadlinesAsync(m_session, actor.Id, ct)
            : new Dictionary<Guid, DateTime?>();
        var grants = rows
            .Select(g => Grant(g.Id, g.UserId, g.GroupId, spaceId, AccessScopeKind.SpaceDefaults, null, g.Role, g.StartAt,
                GrantQueryLimits.Deadline(g.GroupId, g.ExpiresAt, groups)))
            .ToArray();
        return new ResourceGrantSnapshot(Principal(actor, sources, ResourceGrantSnapshot.GroupsOf(grants)), grants);
    }

    /// <summary>Current ResourceGrant rows of the account on the given pages of one space: one query, chunked IN lists.</summary>
    public async Task<IReadOnlyCollection<ResourceAccessGrant>> PageGrantsAsync(UserAccount actor, Guid spaceId, IReadOnlyCollection<Guid> pageIds,
        CancellationToken ct)
    {
        if (!actor.IsActive || actor.IsWikiAdmin || pageIds.Count == 0)
        {
            return [];
        }
        var rows = await m_session.QueryOver<ResourceGrant>()
            .Where(g => g.ActiveTo == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<ResourceGrant>(g => g.PageId), pageIds.Distinct().ToArray()))
            .And(GrantQueryLimits.CurrentlyValid())
            .And(GrantQueryLimits.OfAccount(actor.Id))
            .ListAsync(ct);
        var groups = rows.Any(g => g.GroupId.HasValue)
            ? await GrantQueryLimits.GroupDeadlinesAsync(m_session, actor.Id, ct)
            : new Dictionary<Guid, DateTime?>();
        return rows
            .Select(g => Grant(g.Id, g.UserId, g.GroupId, spaceId, AccessScopeKind.Resource, g.PageId, g.Role, g.StartAt,
                GrantQueryLimits.Deadline(g.GroupId, g.ExpiresAt, groups)))
            .ToArray();
    }

    private static ResourceAccessPrincipal Principal(UserAccount actor, WikiAdminSources sources, IEnumerable<Guid> groupIds)
    {
        return new ResourceAccessPrincipal(AccessContextKind.Internal, actor.Id, actor.IsActive, sources.Persisted, groupIds.ToHashSet(),
            sources.HubVerifiedUntil is { } until ? new DateTimeOffset(until, TimeSpan.Zero) : null);
    }

    private static ResourceAccessGrant Grant(Guid id, Guid? userId, Guid? groupId, Guid spaceId, AccessScopeKind scope,
        Guid? pageId, int role, DateTime? startAt, DateTime? expiresAt)
    {
        var subjectKind = userId is null ? AccessSubjectKind.Group : AccessSubjectKind.User;
        // Unknown stored roles map to no role, which the policy denies.
        var documentRole = role switch
        {
            1 => AccessRole.DocumentReader,
            2 => AccessRole.DocumentWriter,
            3 => AccessRole.DocumentManager,
            _ => (AccessRole)0
        };
        return new ResourceAccessGrant(id, subjectKind, userId ?? groupId ?? Guid.Empty, spaceId, scope,
            AccessResourceKind.Document, pageId, documentRole,
            startAt is { } start ? new DateTimeOffset(start, TimeSpan.Zero) : DateTimeOffset.MinValue,
            expiresAt is { } expiry ? new DateTimeOffset(expiry, TimeSpan.Zero) : null);
    }
}
