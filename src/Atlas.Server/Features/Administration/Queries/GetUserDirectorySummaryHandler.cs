using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class GetUserDirectorySummaryHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public GetUserDirectorySummaryHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<UserDirectorySummary> Handle(GetUserDirectorySummary query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var users = () => m_session.QueryOver<AdministrationUserView>();
        return new UserDirectorySummary(
            await users().RowCountAsync(ct),
            await users().Where(x => x.IsActive == true && x.HasBuiltinCredential == true && x.HasPassword == false).RowCountAsync(ct),
            await users().Where(x => x.IsActive == false).RowCountAsync(ct),
            await users().Where(x => x.IsWikiAdmin == true).RowCountAsync(ct));
    }
}
