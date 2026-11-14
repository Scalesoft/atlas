using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Queries;

public sealed class GetObjectVersionHandler
{
    private readonly ISession m_session;

    public GetObjectVersionHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<ObjectVersion> Handle(GetObjectVersion query, CancellationToken ct)
    {
        return new ObjectVersion(await m_session.QueryOver<ContentResource>().Where(x => x.Id == query.ObjectId).Select(x => x.Version).SingleOrDefaultAsync<long?>(ct));
    }
}
