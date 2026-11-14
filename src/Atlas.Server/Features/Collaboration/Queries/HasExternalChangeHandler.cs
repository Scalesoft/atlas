using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Queries;

public sealed class HasExternalChangeHandler
{
    private readonly ISession m_session;

    public HasExternalChangeHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<ExternalChange> Handle(HasExternalChange query, CancellationToken ct)
    {
        var generation = await m_session.QueryOver<PageCollaboration>()
            .Where(x => x.PageId == query.PageId)
            .Select(x => x.Generation)
            .SingleOrDefaultAsync<Guid>(ct);
        return new ExternalChange(generation != query.Generation);
    }
}
