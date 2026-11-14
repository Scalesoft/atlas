using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Attachments.Queries;

public sealed class ListPageAttachmentsHandler
{
    private readonly AttachmentCatalog m_catalog;

    public ListPageAttachmentsHandler(AttachmentCatalog catalog)
    {
        m_catalog = catalog;
    }

    public Task<PageAttachmentsDto> Handle(ListPageAttachments query, CancellationToken ct)
    {
        return m_catalog.ListAsync(query.PageId, ct);
    }
}
