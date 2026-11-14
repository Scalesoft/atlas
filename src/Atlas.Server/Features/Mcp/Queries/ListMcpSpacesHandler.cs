using Atlas.Server.Features.Spaces.Domain;
using NHibernate.Criterion;
using Atlas.Server.Features.Mcp.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>The personal API's space list (ListApiSpacesHandler: space roles and spaces reached through single pages, D8) with the
/// G5 filter inside the paged query, so a page of results is never short of what follows.</summary>
public sealed class ListMcpSpacesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly SpaceRoles m_roles;
    private readonly PageListing m_listing;

    public ListMcpSpacesHandler(ISession session, IContentAccess access, SpaceRoles roles, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_roles = roles;
        m_listing = listing;
    }

    public async Task<McpSpaceList> Handle(ListMcpSpaces query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        if (query.Offset is < 0 or > 100_000 || query.Limit is < 1 or > 100)
        {
            throw new ApiException(400, "Neplatné stránkování.", "invalid_argument");
        }
        var visible = await ListedSpaces.ReadAsync(m_roles, m_listing, actor, ct);
        if (visible is { Count: 0 })
        {
            return new McpSpaceList([], false);
        }
        var spaces = LiveSpace.Query(m_session).And(Expression.Sql(AccessSql.SpaceInAi("{alias}.\"Id\"")));
        if (visible is not null)
        {
            spaces = spaces.And(GrantQueryLimits.InChunks(Projections.Property<Space>(x => x.Id), visible.Keys.ToList()));
        }
        var rows = await spaces.OrderBy(x => x.Name).Asc.ThenBy(x => x.Id).Asc
            .Skip(query.Offset).Take(query.Limit + 1)
            .ListAsync(ct);
        var items = rows.Take(query.Limit)
            .Select(space => ListedSpaces.Role(visible, space.Id) is { } role
                ? new McpSpaceItem(space.Id, space.Name, space.Description, role.ToString())
                // Only single pages of the space: its name, never its description (D8).
                : new McpSpaceItem(space.Id, space.Name, "", nameof(SpaceRole.Reader)))
            .ToArray();
        return new McpSpaceList(items, rows.Count > query.Limit);
    }
}
