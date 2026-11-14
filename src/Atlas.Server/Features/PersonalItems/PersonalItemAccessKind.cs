using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.PersonalItems.Commands;
using Atlas.Server.Features.PersonalItems.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalItems;

/// <summary>
/// Personal items as an access kind (<c>item</c>, spec 2026-10-29 §3): the grants on one note, read and write. Only
/// the owner lists and changes who it is shared with (SetItemGrantHandler); no administrator has automatic access (R3), so a card
/// shows a personal item only to the person it is shared with (scope self) or to its owner, never another's item to an administrator.
/// The owner is the one locked automatic row of an item.
/// </summary>
public sealed class PersonalItemAccessKind(ISession session, IContentAccess content, PersonalItemTargets targets, SetItemGrantHandler grants)
    : IAccessKind, IAccessGrantWriter
{
    private static readonly AccessKindDescriptor Kind = new("item", "Osobní položka", "Osobní položky", "lock", null, null,
    [
        new(AccessLevels.Read, "Čte poznámku, kterou mu vlastník sdílel"),
        new(AccessLevels.Write, "Navíc ji upravuje; sdílí a maže ji jen vlastník")
    ]);

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter Writer => this;

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        if (scope.SpaceId is not null)
        {
            return [];
        }
        var self = who.IsUser && who.Id == scope.Viewer.Id;
        var (rows, valid) = await AccessGrantRows.ResourceAsync(session, () => Restrictions.And(AccessGrantRows.Of(who)(), Restrictions.IsNotNull("ObjectId")), ct);
        var ids = rows.Select(row => row.ObjectId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        var types = targets.TypeIds().ToList();
        var items = await session.QueryOver<ContentResource>()
            .Where(x => x.Scope == ObjectScopes.Private && x.TrashedAt == null)
            .AndRestrictionOn(x => x.TypeId).IsIn(types)
            .And(GrantQueryLimits.InChunks(Projections.Property<ContentResource>(x => x.Id), ids))
            .ListAsync(ct);
        var shown = items.Where(item => self || item.OwnerId == scope.Viewer.Id).ToArray();
        var owners = await NamesAsync(shown.Select(item => item.OwnerId!.Value), ct);
        var groups = await AccessGrantRows.GroupNamesAsync(session, rows.Select(row => row.GroupId), ct);
        return shown.Select(item =>
        {
            var sources = AccessSources.Ordered(rows.Where(row => row.ObjectId == item.Id).Select(row => Source(row, valid, groups, who.IsUser)));
            return new AccessEntry(Kind.Id, item.Id.ToString(), item.Title ?? "", PersonalItemTargets.Label(item.TypeId), null, null, AccessSources.Level(sources),
                sources, owners.TryGetValue(item.OwnerId!.Value, out var owner) ? $"Sdílí {owner}" : null);
        }).OrderBy(entry => entry.Title, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct)
    {
        var actor = await content.LockActorAsync(null, ct);
        var item = await targets.RequireOwnedAsync(actor, resourceId, false, ct);
        var (rows, valid) = await AccessGrantRows.ResourceAsync(session, () => Restrictions.Eq("ObjectId", item.Id), ct);
        var users = (await session.QueryOver<UserAccount>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<UserAccount>(x => x.Id), rows.Select(row => row.UserId).OfType<Guid>().Distinct().ToList()))
            .ListAsync(ct)).ToDictionary(user => user.Id);
        var groups = await AccessGrantRows.GroupNamesAsync(session, rows.Select(row => row.GroupId), ct);
        var members = await MemberCountsAsync(groups.Keys.ToList(), ct);
        var entries = rows.Select(row =>
        {
            IReadOnlyList<AccessEntrySource> sources = [Source(row, valid, groups, false)];
            if (row.UserId is { } userId)
            {
                var user = users[userId];
                return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.User, user.Id, user.DisplayName, user.Username, null, user.IsActive),
                    user.IsActive ? AccessSources.Level(sources) : null, sources);
            }
            var count = members.GetValueOrDefault(row.GroupId!.Value);
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.Group, row.GroupId.Value, groups.GetValueOrDefault(row.GroupId.Value, ""),
                AccessNotes.Count(count, "člen", "členové", "členů"), count), AccessSources.Level(sources), sources);
        }).OrderBy(entry => entry.Subject.Kind).ThenBy(entry => entry.Subject.Name, StringComparer.CurrentCulture).ToArray();
        return new ResourceAccess(new AccessResource(Kind.Id, item.Id.ToString(), item.Title ?? "", PersonalItemTargets.Label(item.TypeId)), true,
            [new AutomaticAccess("Vlastník", $"{actor.DisplayName}: jediný sdílí, mění přístupy a maže", AccessLevels.Manage, 1)], entries, false);
    }

    public async Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct)
    {
        var term = AccessQuery.Term(query);
        var actor = await content.LockActorAsync(null, ct);
        var types = targets.TypeIds().ToList();
        var items = session.QueryOver<ContentResource>()
            .Where(x => x.Scope == ObjectScopes.Private && x.OwnerId == actor.Id && x.TrashedAt == null)
            .AndRestrictionOn(x => x.TypeId).IsIn(types);
        if (term.Length > 0)
        {
            items = items.And(AccessQuery.Contains("coalesce({alias}.\"Title\", '')", term));
        }
        var found = await items.OrderBy(x => x.UpdatedAt).Desc.ThenBy(x => x.Id).Asc.Take(AccessQuery.Limit).ListAsync(ct);
        return found.Select(item => new AccessResource(Kind.Id, item.Id.ToString(), item.Title ?? "", PersonalItemTargets.Label(item.TypeId))).ToArray();
    }

    public Task ApplyAsync(Guid resourceId, AccessGrantChange change, CancellationToken ct)
    {
        int? role = change.Level switch
        {
            null => null,
            AccessLevels.Read => ObjectGrants.Read,
            AccessLevels.Write => ObjectGrants.Write,
            _ => throw new ApiException(400, "Vyberte úroveň sdílení: read (čte) nebo write (upravuje).")
        };
        return grants.Handle(new SetItemGrant(resourceId, change.UserId, change.GroupId, role, change.StartAt, change.ExpiresAt, change.ExpectedVersion), ct);
    }

    private static AccessEntrySource Source(ResourceGrant row, IReadOnlySet<Guid> valid, IReadOnlyDictionary<Guid, string> groups, bool viaGroup)
    {
        return AccessSources.Grant(row.Id, row.GroupId, row.GroupId is { } id ? groups.GetValueOrDefault(id) : null, AccessLevels.FromRole(row.Role)!,
            row.ActiveFrom, row.StartAt, row.ExpiresAt, valid.Contains(row.Id), viaGroup && row.UserId is null);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return distinct.Count == 0 ? new Dictionary<Guid, string>() : (await session.QueryOver<UserAccount>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<UserAccount>(x => x.Id), distinct))
            .ListAsync(ct)).ToDictionary(user => user.Id, user => user.DisplayName);
    }

    private async Task<IReadOnlyDictionary<Guid, int>> MemberCountsAsync(IList<Guid> groupIds, CancellationToken ct)
    {
        if (groupIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }
        var activeMembers = QueryOver.Of<UserAccount>().Where(u => u.IsActive).Select(u => u.Id);
        var rows = await session.QueryOver<GroupMembership>()
            .Where(m => m.ActiveTo == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<GroupMembership>(m => m.GroupId), groupIds))
            .And(Subqueries.PropertyIn("UserId", activeMembers.DetachedCriteria))
            .SelectList(list => list.SelectGroup(m => m.GroupId).SelectCount(m => m.UserId))
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1]));
    }
}
