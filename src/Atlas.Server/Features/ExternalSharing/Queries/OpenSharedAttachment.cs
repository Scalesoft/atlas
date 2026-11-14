using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing.Queries;

public sealed record OpenSharedAttachment(string TokenHash, Guid AttachmentId, bool Inline);
public sealed class OpenSharedAttachmentHandler(ISession session, ExternalSharingAccess access)
{
    public async Task<AttachmentContentDto> Handle(OpenSharedAttachment query, CancellationToken ct)
    {
        var link = await access.RequirePublicAsync(query.TokenHash, ct);
        var shown = await access.SharedRevisionAsync(link, ct);
        var attachment = await session.GetAsync<Attachment>(query.AttachmentId, ct);
        if (attachment is null || attachment.ActiveTo is not null || attachment.PageId != link.PageId || query.Inline && !attachment.IsImage)
            throw ExternalSharingAccess.Missing();
        var membership = await session.QueryOver<PageRevisionAttachment>()
            .Where(x => x.PageId == link.PageId && x.RevisionId == shown && x.AttachmentId == query.AttachmentId).RowCountAsync(ct);
        if (membership == 0) throw ExternalSharingAccess.Missing();
        return new AttachmentContentDto(attachment.Id, attachment.Name, attachment.MimeType, attachment.Sha256, attachment.ActiveFrom, link.ExpiresAt);
    }
}
