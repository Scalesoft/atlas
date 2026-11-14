using Atlas.Server.Features.Attachments.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>An attachment the actor may open, with the page decision behind it. Until bounds the authority (a download's lease).</summary>
public sealed record OpenedAttachment(Attachment Attachment, Page Page, UserAccount Actor, bool CanEdit, bool Published, DateTime? Until);

/// <summary>
/// What an actor may know about the attachments of a page: the one decision behind the download (<c>OpenAttachment</c>), the page's
/// attachment list and the metadata of one attachment, so the three never drift apart. The page comes first
/// (<see cref="AttachmentPageGuard"/>: trashed, invisible and private-draft pages are missing), so knowing an attachment id never skips
/// it. An editor then sees every active attachment of the page; anyone else only those the published revision references, so an
/// upload or a reference only the working revision makes never reaches a reader. Each attachment is decided once more on the actor's
/// grants for the operation its revision needs (ReadContent for the publication, EditContent otherwise), which also bounds a lease.
/// </summary>
public sealed class AttachmentCatalog
{
    private readonly ISession m_session;
    private readonly AttachmentPageGuard m_guard;

    public AttachmentCatalog(ISession session, AttachmentPageGuard guard)
    {
        m_session = session;
        m_guard = guard;
    }

    /// <summary>One attachment by id; <paramref name="inline"/> asks for an image to show. Every refusal is the guard's 404.</summary>
    public async Task<OpenedAttachment> OpenAsync(Guid id, bool inline, CancellationToken ct)
    {
        var attachment = await m_session.GetAsync<Attachment>(id, ct);
        if (attachment is null || attachment.ActiveTo is not null || inline && !attachment.IsImage)
        {
            throw AttachmentPageGuard.Missing();
        }
        var (page, actor, canEdit) = await m_guard.RequirePageAsync(attachment.PageId, AccessOperation.ReadContent, AttachmentPageLock.Shared, ct);
        // One statement for the publication only: the image of an article is opened on every use (AttachmentHttpTests' budget).
        var published = page.PublishedRevisionId is { } revision && await m_session.QueryOver<PageRevisionAttachment>()
            .Where(x => x.RevisionId == revision && x.AttachmentId == attachment.Id).RowCountAsync(ct) > 0;
        var grants = await new ResourceGrantSnapshots(m_session).ForPageAsync(actor, page, ct);
        var (allowed, until) = Authority(page, canEdit, published, grants, DateTimeOffset.UtcNow);
        if (!allowed)
        {
            throw AttachmentPageGuard.Missing();
        }
        return new OpenedAttachment(attachment, page, actor, canEdit, published, until);
    }

    /// <summary>The metadata of one attachment; whether the working revision uses it only for an actor who may see that revision.</summary>
    public async Task<AttachmentInfoDto> DescribeAsync(Guid id, CancellationToken ct)
    {
        var opened = await OpenAsync(id, false, ct);
        bool? working = null;
        if (opened.CanEdit)
        {
            var revisions = await RevisionsAsync(opened.Page, true, ct);
            working = revisions.Working is { } current && await m_session.QueryOver<PageRevisionAttachment>()
                .Where(x => x.RevisionId == current && x.AttachmentId == opened.Attachment.Id).RowCountAsync(ct) > 0;
        }
        return Describe(opened.Attachment, opened.Published, working);
    }

    /// <summary>The attachments of a page the actor may open, oldest upload first, each marked with the revisions that use it.</summary>
    public async Task<PageAttachmentsDto> ListAsync(Guid pageId, CancellationToken ct)
    {
        var (page, actor, canEdit) = await m_guard.RequirePageAsync(pageId, AccessOperation.ReadContent, AttachmentPageLock.Shared, ct);
        var (published, working) = await RevisionsAsync(page, canEdit, ct);
        var revisions = new[] { published, working }.OfType<Guid>().Distinct().ToArray();
        var uses = revisions.Length == 0
            ? []
            : await m_session.QueryOver<PageRevisionAttachment>().Where(x => x.PageId == page.Id)
                .WhereRestrictionOn(x => x.RevisionId).IsIn(revisions).ListAsync(ct);
        var inPublication = uses.Where(x => x.RevisionId == published).Select(x => x.AttachmentId).ToHashSet();
        var inWorking = uses.Where(x => x.RevisionId == working).Select(x => x.AttachmentId).ToHashSet();
        var query = m_session.QueryOver<Attachment>().Where(x => x.PageId == page.Id && x.ActiveTo == null);
        if (!canEdit)
        {
            // A reader is never shown what only the working revision or an upload holds, not even as a count.
            query = query.WhereRestrictionOn(x => x.Id).IsIn(inPublication.ToArray());
        }
        var attachments = !canEdit && inPublication.Count == 0
            ? []
            : await query.OrderBy(x => x.ActiveFrom).Asc.ThenBy(x => x.Id).Asc.ListAsync(ct);
        var grants = await new ResourceGrantSnapshots(m_session).ForPageAsync(actor, page, ct);
        var now = DateTimeOffset.UtcNow;
        var items = attachments
            .Where(attachment => Authority(page, canEdit, inPublication.Contains(attachment.Id), grants, now).Allowed)
            .Select(attachment => Describe(attachment, inPublication.Contains(attachment.Id), canEdit ? inWorking.Contains(attachment.Id) : null))
            .ToArray();
        return new PageAttachmentsDto(page.Id, items);
    }

    /// <summary>The download decision for one attachment: a reader only through the publication, an editor on EditContent otherwise.</summary>
    private static (bool Allowed, DateTime? Until) Authority(Page page, bool canEdit, bool published, ResourceGrantSnapshot grants, DateTimeOffset now)
    {
        if (!canEdit && !published)
        {
            return (false, null);
        }
        return grants.Authority(page, published ? AccessOperation.ReadContent : AccessOperation.EditContent, now);
    }

    /// <summary>The published revision and, for an editor, the working one (the current revision row).</summary>
    private async Task<(Guid? Published, Guid? Working)> RevisionsAsync(Page page, bool canEdit, CancellationToken ct)
    {
        Guid? working = null;
        if (canEdit)
        {
            var pageId = page.Id;
            working = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null)
                .Select(x => x.Id).SingleOrDefaultAsync<Guid?>(ct);
        }
        return (page.PublishedRevisionId, working);
    }

    private static AttachmentInfoDto Describe(Attachment attachment, bool published, bool? working)
    {
        var urls = AttachmentUrls.Describe(attachment);
        return new AttachmentInfoDto(attachment.Id, attachment.PageId, attachment.Name, attachment.MimeType, attachment.Size, attachment.IsImage,
            attachment.Sha256, attachment.ActiveFrom, urls.Url, urls.DownloadUrl, published, working);
    }
}
