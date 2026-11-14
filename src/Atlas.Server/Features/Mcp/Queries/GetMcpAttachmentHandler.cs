using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>
/// The shared metadata (<see cref="AttachmentCatalog"/>: the page decision first, so knowing an id grants nothing), then G5 of the
/// attachment's page in the same transaction. An unknown id, an attachment of a page the actor may not see or of a space excluded
/// from MCP, and a draft-only attachment asked for by a reader all answer the one 404 of the download.
/// </summary>
public sealed class GetMcpAttachmentHandler
{
    private readonly AttachmentCatalog m_catalog;
    private readonly McpVisibility m_visibility;

    public GetMcpAttachmentHandler(AttachmentCatalog catalog, McpVisibility visibility)
    {
        m_catalog = catalog;
        m_visibility = visibility;
    }

    public async Task<AttachmentInfoDto> Handle(GetMcpAttachment query, CancellationToken ct)
    {
        try
        {
            var attachment = await m_catalog.DescribeAsync(query.AttachmentId, ct);
            await m_visibility.RequirePageAsync(attachment.PageId, ct);
            return attachment;
        }
        catch (ApiException error) when (error.Status is 403 or 404)
        {
            throw AttachmentPageGuard.Missing();
        }
    }
}
