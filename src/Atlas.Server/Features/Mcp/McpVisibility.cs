using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// G5 for MCP (D10): a space switched out of "Zahrnout do AI a nástrojů MCP" is invisible to MCP. Every decision goes through the one
/// SQL definition, <see cref="AccessSql.SpaceInAi"/> (migration 2026100530), in the caller's transaction, so a switch applies to the next call.
/// An excluded space or page answers the same 404 as one the actor may not read; lists simply leave it out. Content rights are decided
/// elsewhere (the shared handlers); this only narrows them.
/// </summary>
public sealed class McpVisibility
{
    public const string MissingSpace = "Prostor nebyl nalezen.";
    public const string MissingPage = "Stránka nebyla nalezena.";

    private readonly ISession m_session;

    public McpVisibility(ISession session)
    {
        m_session = session;
    }

    public async Task RequireSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        var included = await m_session.CreateSQLQuery($"SELECT {AccessSql.SpaceInAi(":space")} AS included")
            .AddScalar("included", NHibernateUtil.Boolean)
            .SetParameter("space", spaceId)
            .UniqueResultAsync<bool>(ct);
        if (!included)
        {
            throw new ApiException(404, MissingSpace);
        }
    }

    public async Task RequirePageAsync(Guid pageId, CancellationToken ct)
    {
        if (!(await PagesInScopeAsync([pageId], ct)).Contains(pageId))
        {
            throw new ApiException(404, MissingPage);
        }
    }

    /// <summary>The existing pages among <paramref name="pageIds"/> whose space takes part in AI and MCP.</summary>
    public async Task<IReadOnlySet<Guid>> PagesInScopeAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0)
        {
            return new HashSet<Guid>();
        }
        var rows = await m_session.CreateSQLQuery($$"""SELECT p."Id" AS id FROM "Page" p WHERE p."Id" IN (:ids) AND {{AccessSql.SpaceInAi("p.\"SpaceId\"")}}""")
            .AddScalar("id", NHibernateUtil.Guid)
            .SetParameterList("ids", pageIds.Distinct().ToArray())
            .ListAsync<Guid>(ct);
        return rows.ToHashSet();
    }

    /// <summary>The spaces among <paramref name="spaceIds"/> switched out of AI and MCP.</summary>
    public async Task<IReadOnlySet<Guid>> ExcludedSpacesAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
    {
        if (spaceIds.Count == 0)
        {
            return new HashSet<Guid>();
        }
        var rows = await m_session.CreateSQLQuery($$"""SELECT s."Id" AS id FROM "Space" s WHERE s."Id" IN (:ids) AND NOT {{AccessSql.SpaceInAi("s.\"Id\"")}}""")
            .AddScalar("id", NHibernateUtil.Guid)
            .SetParameterList("ids", spaceIds.Distinct().ToArray())
            .ListAsync<Guid>(ct);
        return rows.ToHashSet();
    }
}
