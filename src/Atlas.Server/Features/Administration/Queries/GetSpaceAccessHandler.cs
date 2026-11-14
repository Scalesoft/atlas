using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class GetSpaceAccessHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public GetSpaceAccessHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<SpaceAccessSummary> Handle(GetSpaceAccess query, CancellationToken ct)
    {
        await m_access.RequireSpaceAdminAsync(query.SpaceId, false, ct);
        var grants = await m_session.QueryOver<SpaceGrantView>()
            .Where(x => x.SpaceId == query.SpaceId)
            .OrderBy(x => x.Id).Asc
            .Take(AdministrationReads.PageLimit)
            .ListAsync(ct);
        return new SpaceAccessSummary(grants.Select(x => x.ToSummary()).ToArray());
    }
}
