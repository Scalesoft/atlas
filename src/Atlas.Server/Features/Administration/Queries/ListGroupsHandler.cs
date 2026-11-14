using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class ListGroupsHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public ListGroupsHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<IReadOnlyList<GroupSummary>> Handle(ListGroups query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var groups = await m_session.QueryOver<AdministrationGroupView>()
            .OrderBy(x => x.Name).Asc
            .ThenBy(x => x.Id).Asc
            .Take(AdministrationReads.PageLimit)
            .ListAsync(ct);
        return groups.Select(x => x.ToSummary()).ToArray();
    }
}
