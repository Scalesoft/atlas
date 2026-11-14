using Atlas.Server.Features.Spaces.Domain;
using NHibernate.Criterion;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>
/// The grant rows the access kinds explain: every current assignment (ActiveTo null) of a filter, with the ids of those valid at the
/// database statement clock (GrantQueryLimits.CurrentlyValid, as the Effective* views), so a card lists a grant that has not started
/// or has expired as not effective instead of hiding it. Group grants of a person match its current groups in the same statement
/// (GrantQueryLimits.OfAccount), exactly as SpaceRoles, ResourceGrantSnapshots and VaultGrantSnapshots load them.
/// </summary>
public static class AccessGrantRows
{
    /// <summary>A card lists at most this many rows per kind, as the administration lists.</summary>
    public const int Limit = 1000;

    /// <summary>The grants of the principal itself and, for a person, of its current groups.</summary>
    public static Func<ICriterion> Of(AccessPrincipal who)
    {
        return who.IsUser ? () => GrantQueryLimits.OfAccount(who.Id) : () => Restrictions.Eq("GroupId", who.Id);
    }

    public static async Task<(IList<SpaceGrant> Rows, IReadOnlySet<Guid> Valid)> SpaceAsync(ISession session, Func<ICriterion> filter,
        CancellationToken ct)
    {
        var rows = await session.QueryOver<SpaceGrant>().Where(g => g.ActiveTo == null).And(filter()).Take(Limit).ListAsync(ct);
        var valid = await session.QueryOver<SpaceGrant>().Where(g => g.ActiveTo == null).And(filter()).And(GrantQueryLimits.CurrentlyValid())
            .Select(g => g.Id).ListAsync<Guid>(ct);
        return (rows, valid.ToHashSet());
    }

    public static async Task<(IList<ResourceGrant> Rows, IReadOnlySet<Guid> Valid)> ResourceAsync(ISession session, Func<ICriterion> filter,
        CancellationToken ct)
    {
        var rows = await session.QueryOver<ResourceGrant>().Where(g => g.ActiveTo == null).And(filter()).Take(Limit).ListAsync(ct);
        var valid = await session.QueryOver<ResourceGrant>().Where(g => g.ActiveTo == null).And(filter()).And(GrantQueryLimits.CurrentlyValid())
            .Select(g => g.Id).ListAsync<Guid>(ct);
        return (rows, valid.ToHashSet());
    }

    public static async Task<IReadOnlyDictionary<Guid, string>> GroupNamesAsync(ISession session, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var distinct = ids.OfType<Guid>().Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }
        var groups = await session.QueryOver<UserGroup>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<UserGroup>(g => g.Id), distinct))
            .ListAsync(ct);
        return groups.ToDictionary(g => g.Id, g => g.Name);
    }

    public static async Task<IReadOnlyDictionary<Guid, Space>> SpacesAsync(ISession session, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return new Dictionary<Guid, Space>();
        }
        // A trashed space is left out, so its grants (kept for the restore) are listed nowhere meanwhile.
        var spaces = await LiveSpace.Query(session)
            .And(GrantQueryLimits.InChunks(Projections.Property<Space>(s => s.Id), distinct))
            .ListAsync(ct);
        return spaces.ToDictionary(s => s.Id);
    }

    /// <summary>The effective space role of the principal per space: a person's as SpaceRoles decides it for every space check
    /// (nothing for an inactive account; a WikiAdmin's grants only, callers treat WikiAdmin themselves), a group's as the highest of its
    /// own valid grants.</summary>
    public static async Task<IReadOnlyDictionary<Guid, SpaceRole>> SpaceRolesAsync(ISession session, SpaceRoles roles, AccessPrincipal who,
        CancellationToken ct)
    {
        if (who.Account is { } account)
        {
            return await roles.EffectiveAsync(account, ct);
        }
        var rows = await session.QueryOver<SpaceGrant>()
            .Where(g => g.GroupId == who.Id && g.ActiveTo == null)
            .And(GrantQueryLimits.CurrentlyValid())
            .SelectList(list => list.SelectGroup(g => g.SpaceId).SelectMax(g => g.Role))
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (SpaceRole)Convert.ToInt32(row[1]));
    }
}
