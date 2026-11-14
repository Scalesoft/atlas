using Atlas.Server.Features.Pages.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_get_publication: the shared read of the page's „Publikace“, G5 decided after it under its locks, as atlas_get_labels.</summary>
public sealed record GetMcpPublication(Guid PageId);

public sealed class GetMcpPublicationHandler(ISession session, McpVisibility visibility, PagePublicationSettingHandler handler)
{
    public async Task<PagePublicationState> Handle(GetMcpPublication query, CancellationToken ct)
    {
        var state = await handler.Handle(new GetPagePublication(query.PageId), ct);
        var page = (await session.GetAsync<Page>(query.PageId, ct))!;
        try
        {
            await visibility.RequireSpaceAsync(page.SpaceId, ct);
        }
        catch (ApiException)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
        return state;
    }
}
