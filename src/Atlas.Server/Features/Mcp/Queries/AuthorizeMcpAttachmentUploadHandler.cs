using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>
/// The shared upload guard (<see cref="AttachmentPageGuard"/>, EditContent, unlocked as the REST preflight), then G5 in the same
/// transaction. A page that is missing, trashed, invisible, only readable or in an excluded space is one 404, as for the draft tools.
/// Nothing touches the disk before this; StoreMcpAttachment decides it all again under the page locks.
/// </summary>
public sealed class AuthorizeMcpAttachmentUploadHandler
{
    private readonly AttachmentPageGuard m_guard;
    private readonly McpVisibility m_visibility;

    public AuthorizeMcpAttachmentUploadHandler(AttachmentPageGuard guard, McpVisibility visibility)
    {
        m_guard = guard;
        m_visibility = visibility;
    }

    public async Task Handle(AuthorizeMcpAttachmentUpload query, CancellationToken ct)
    {
        try
        {
            var (page, _, _) = await m_guard.RequirePageAsync(query.PageId, AccessOperation.EditContent, AttachmentPageLock.None, ct);
            await m_visibility.RequireSpaceAsync(page.SpaceId, ct);
        }
        catch (ApiException error) when (error.Status == 404)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
    }
}
