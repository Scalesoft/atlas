using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Attachments.Queries;

public sealed class GetAttachmentInfoHandler
{
    private readonly AttachmentCatalog m_catalog;

    public GetAttachmentInfoHandler(AttachmentCatalog catalog)
    {
        m_catalog = catalog;
    }

    public Task<AttachmentInfoDto> Handle(GetAttachmentInfo query, CancellationToken ct)
    {
        return m_catalog.DescribeAsync(query.Id, ct);
    }
}
