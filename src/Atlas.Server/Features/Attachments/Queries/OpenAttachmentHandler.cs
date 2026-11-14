using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Attachments.Queries;

public sealed class OpenAttachmentHandler
{
    private readonly ISession m_session;
    private readonly AttachmentCatalog m_catalog;

    public OpenAttachmentHandler(ISession session, AttachmentCatalog catalog)
    {
        m_session = session;
        m_catalog = catalog;
    }

    public async Task<AttachmentContentDto> Handle(OpenAttachment query, CancellationToken ct)
    {
        var opened = await m_catalog.OpenAsync(query.Id, query.Inline, ct);
        var lease = new DownloadLease(opened.Until).Limit(await DownloadCredentials.ReadAsync(m_session, opened.Actor, query.AuthenticatedTokenId, ct));
        var attachment = opened.Attachment;
        return new AttachmentContentDto(attachment.Id, attachment.Name, attachment.MimeType, attachment.Sha256, attachment.ActiveFrom, lease.Until);
    }
}
