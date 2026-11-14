using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class ListUsersHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public ListUsersHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<IReadOnlyList<UserSummary>> Handle(ListUsers query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var rows = m_session.QueryOver<AdministrationUserView>();
        if (AdministrationReads.Search(query.Search) is { } match) rows = rows.And(match);
        if (query.Id is { } id) rows = rows.Where(x => x.Id == id);
        var users = await rows
            .OrderBy(x => x.DisplayName).Asc
            .ThenBy(x => x.Id).Asc
            .Skip(Math.Max(0, query.Offset))
            .Take(AdministrationReads.PageLimit)
            .ListAsync(ct);
        return users.Select(x => x.ToSummary()).ToArray();
    }
}
