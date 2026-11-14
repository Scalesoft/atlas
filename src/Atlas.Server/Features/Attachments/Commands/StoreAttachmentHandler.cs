using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Attachments.Commands;

public sealed class StoreAttachmentHandler
{
    private readonly ISession m_session;
    private readonly AttachmentPageGuard m_guard;
    private readonly AttachmentFiles m_files;

    public StoreAttachmentHandler(ISession session, AttachmentPageGuard guard, AttachmentFiles files)
    {
        m_session = session;
        m_guard = guard;
        m_files = files;
    }

    public async Task<AttachmentDto> Handle(StoreAttachment command, CancellationToken ct)
    {
        try
        {
            var (page, actor, _) = await m_guard.RequirePageAsync(command.PageId, AccessOperation.EditContent, AttachmentPageLock.Update, ct);
            var attachment = new Attachment
            {
                Id = command.Id,
                PageId = page.Id,
                UploadedBy = actor.Id,
                Name = AttachmentFiles.SafeName(command.Name),
                Size = command.Size,
                Sha256 = command.Sha256,
                MimeType = command.MimeType,
                IsImage = command.IsImage,
                ActiveFrom = DateTime.UtcNow
            };
            await m_session.SaveAsync(attachment, ct);
            await m_session.FlushAsync(ct);
            return AttachmentUrls.Describe(attachment);
        }
        catch
        {
            // The middleware has not committed yet, so the blob is provably unreferenced.
            // A failing COMMIT leaves an orphan on purpose: an orphan is recoverable, deleting committed content is not.
            m_files.Remove(command.Id);
            throw;
        }
    }
}
