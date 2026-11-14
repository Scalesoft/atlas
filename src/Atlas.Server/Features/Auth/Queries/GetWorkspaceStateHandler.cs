using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Queries;

public sealed class GetWorkspaceStateHandler
{
    private readonly ISession m_session;

    public GetWorkspaceStateHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<WorkspaceState> Handle(GetWorkspaceState query, CancellationToken ct)
    {
        var workspace = await m_session.GetAsync<Workspace>(1, ct);
        return new WorkspaceState(workspace is not null, workspace?.Name);
    }
}
