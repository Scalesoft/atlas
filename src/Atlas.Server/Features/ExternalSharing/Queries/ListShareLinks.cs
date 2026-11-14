using Atlas.Server.Features.ExternalSharing.Contracts;

namespace Atlas.Server.Features.ExternalSharing.Queries;

public sealed record ListShareLinks(Guid SpaceId, Guid? PageId, int Offset = 0, int Limit = 50);
public sealed class ListShareLinksHandler(ExternalSharingAccess access, ShareLinkReader reader)
{
    public async Task<ShareLinksDto> Handle(ListShareLinks query, CancellationToken ct)
    {
        var spaceId = query.SpaceId;
        if (query.PageId is { } pageId)
        {
            var (page, _) = await access.RequirePageShareAsync(pageId, false, ct);
            spaceId = page.SpaceId;
        }
        else await access.RequireSpaceAdminAsync(spaceId, false, ct);
        return await reader.ReadAsync(spaceId, query.PageId, null, query.Offset, query.Limit, ct);
    }
}
