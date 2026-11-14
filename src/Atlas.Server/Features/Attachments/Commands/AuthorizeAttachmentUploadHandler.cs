using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Attachments.Commands;

public sealed class AuthorizeAttachmentUploadHandler
{
    private readonly AttachmentPageGuard m_guard;

    public AuthorizeAttachmentUploadHandler(AttachmentPageGuard guard)
    {
        m_guard = guard;
    }

    /// <summary>Rejects unauthorized uploads before anything touches the disk. StoreAttachment rechecks under locks.</summary>
    public async Task Handle(AuthorizeAttachmentUpload command, CancellationToken ct)
    {
        await m_guard.RequirePageAsync(command.PageId, AccessOperation.EditContent, AttachmentPageLock.None, ct);
    }
}
