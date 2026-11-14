using Atlas.Server.Features.Spaces.Domain;
using NHibernate.Criterion;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed class ListApiSpacesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly SpaceRoles m_roles;
    private readonly PageListing m_listing;

    public ListApiSpacesHandler(ISession session, IContentAccess access, SpaceRoles roles, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_roles = roles;
        m_listing = listing;
    }

    public async Task<ApiList<ApiSpace>> Handle(ListApiSpaces query, CancellationToken ct)
    {
        // The actor is resolved first, exactly as the endpoint argument did: a revoked credential
        // answers 401 before a malformed page window answers 400.
        var actor = await m_access.LockActorAsync(null, ct);
        var (skip, take) = ApiPagination.Resolve(query.Offset, query.Limit);
        // Space roles and spaces reached through single pages (D8), under one snapshot and the same locks.
        var visible = await ListedSpaces.ReadAsync(m_roles, m_listing, actor, ct);
        var spaces = await VisibleSpacesAsync(visible, skip, take + 1, ct);
        var items = spaces.Take(take)
            .Select(space => ListedSpaces.Role(visible, space.Id) is { } role
                ? new ApiSpace(space.Id, space.Name, space.Icon, space.Color, role.ToString(), space.Description)
                : new ApiSpace(space.Id, space.Name, space.Icon, space.Color, nameof(SpaceRole.Reader)) { ItemAccessOnly = true })
            .ToArray();
        return new ApiList<ApiSpace>(items, skip, take, spaces.Count > take);
    }

    private async Task<IList<Space>> VisibleSpacesAsync(IReadOnlyDictionary<Guid, SpaceRole?>? visible, int skip, int take, CancellationToken ct)
    {
        if (visible is { Count: 0 })
        {
            return [];
        }
        var spaces = LiveSpace.Query(m_session);
        if (visible is not null)
        {
            spaces = spaces.And(GrantQueryLimits.InChunks(Projections.Property<Space>(x => x.Id), visible.Keys.ToList()));
        }
        return await spaces
            .OrderBy(x => x.Name).Asc
            .ThenBy(x => x.Id).Asc
            .Skip(skip)
            .Take(take)
            .ListAsync(ct);
    }
}
