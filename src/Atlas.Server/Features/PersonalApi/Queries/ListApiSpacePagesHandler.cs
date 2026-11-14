using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed class ListApiSpacePagesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ICurrentActor m_currentActor;
    private readonly PageListing m_listing;

    public ListApiSpacePagesHandler(ISession session, IContentAccess access, ICurrentActor currentActor, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_currentActor = currentActor;
        m_listing = listing;
    }

    public async Task<ApiList<ApiPageEntry>> Handle(ListApiSpacePages query, CancellationToken ct)
    {
        // Legacy resolved the actor in the endpoint argument, validated the window, and only then locked the
        // space, so the statuses stay ordered 401 (credential) -> 400 (window) -> 404 (space).
        await m_currentActor.RequireAsync(ct);
        var (skip, take) = ApiPagination.Resolve(query.Offset, query.Limit);
        var actor = await m_access.LockActorAsync(query.SpaceId, ct);
        await m_listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        var rows = await SpacePageList.ReadAsync(m_session, actor, query.SpaceId, skip, take + 1, ct);
        var items = rows.Take(take)
            .Select(entry => new ApiPageEntry(entry.Id, entry.SpaceId, entry.ParentId, entry.Title, entry.Version, entry.IsPublished))
            .ToArray();
        return new ApiList<ApiPageEntry>(items, skip, take, rows.Count > take);
    }
}
