using Atlas.Server.Features.Spaces.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Queries;

public sealed class GetSpacePagesHandler
{
    private const int MaxPages = 1000;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly PageListing m_listing;

    public GetSpacePagesHandler(ISession session, IContentAccess access, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_listing = listing;
    }

    public async Task<SpacePages> Handle(GetSpacePages query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(query.SpaceId, ct);
        await m_listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        return new SpacePages(await SpacePageList.ReadAsync(m_session, actor, query.SpaceId, 0, MaxPages, ct));
    }
}
