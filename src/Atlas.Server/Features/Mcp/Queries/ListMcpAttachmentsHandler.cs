using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>The shared list (<see cref="AttachmentCatalog"/>, the decision of the download), then G5 in the same transaction under the
/// page lock the list holds. A missing, invisible or excluded page is one 404.</summary>
public sealed class ListMcpAttachmentsHandler
{
    private readonly AttachmentCatalog m_catalog;
    private readonly McpVisibility m_visibility;

    public ListMcpAttachmentsHandler(AttachmentCatalog catalog, McpVisibility visibility)
    {
        m_catalog = catalog;
        m_visibility = visibility;
    }

    public async Task<PageAttachmentsDto> Handle(ListMcpAttachments query, CancellationToken ct)
    {
        try
        {
            var list = await m_catalog.ListAsync(query.PageId, ct);
            await m_visibility.RequirePageAsync(query.PageId, ct);
            return list;
        }
        catch (ApiException error) when (error.Status is 403 or 404)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
    }
}
