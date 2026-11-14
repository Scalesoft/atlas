using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class ListGroupMembersHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public ListGroupMembersHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<IReadOnlyList<GroupMemberSummary>> Handle(ListGroupMembers query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        await AdministrationReads.GroupAsync(m_session, query.GroupId, ct);
        var rows = m_session.QueryOver<GroupMemberView>().Where(x => x.GroupId == query.GroupId);
        if (AdministrationReads.Search(query.Search) is { } match) rows = rows.And(match);
        var members = await rows
            .OrderBy(x => x.DisplayName).Asc
            .ThenBy(x => x.UserId).Asc
            .Skip(Math.Max(0, query.Offset))
            .Take(AdministrationReads.PageLimit)
            .ListAsync(ct);
        return members.Select(x => x.ToSummary()).ToArray();
    }
}
