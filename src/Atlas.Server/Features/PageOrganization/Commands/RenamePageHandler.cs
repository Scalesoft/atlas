using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using Atlas.Server.Features.Search;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

public sealed class RenamePageHandler
{
    private readonly ISession m_session;
    private readonly OrganizationLocks m_locks;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly IRequestChannel m_channel;
    private readonly Outbox m_outbox;

    public RenamePageHandler(ISession session, OrganizationLocks locks, CollaborationCheckpoints checkpoints, IRequestChannel channel, Outbox outbox)
    {
        m_outbox = outbox;
        m_channel = channel;
        m_session = session;
        m_locks = locks;
        m_checkpoints = checkpoints;
    }

    /// <summary>Title-only working revision. The endpoint reads the page back with GetPage, exactly like today's second transaction.
    /// No live-edit guard on any channel: a rename only advances the checkpoint base, it never resets the generation or deletes the log;
    /// while a live write's update is on its way it waits (collaboration_pending).</summary>
    public async Task Handle(RenamePage command, CancellationToken ct)
    {
        var (page, actor) = await m_locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        Rules.ExpectVersion(page.Version, command.Request.ExpectedVersion);
        var pageId = page.Id;
        var previous = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        var title = Rules.Required(command.Request.Title, "Název stránky", unchanged: previous.Title);
        if (previous.Title == title)
        {
            return;
        }
        // A live write's revision whose update is not in the live document yet (LivePageWrites): advancing the base past it would claim
        // the live document holds it, and a checkpoint of the live document would then drop it. The rename waits for the append.
        if (await m_session.GetAsync<PageCollaboration>(pageId, ct) is { } live && live.BaseRevisionId != previous.Id)
        {
            throw new ApiException(409, "Společné změny se ještě ukládají.", "collaboration_pending");
        }
        var boundary = Temporal.NextBoundary(previous.ActiveFrom, DateTime.UtcNow);
        previous.ActiveTo = boundary;
        await m_session.FlushAsync(ct);
        page.Version++;
        var revision = new PageRevision
        {
            Id = Guid.NewGuid(),
            PageId = page.Id,
            Version = page.Version,
            Title = title,
            ContentJson = previous.ContentJson,
            Markdown = Projection(previous),
            AuthorId = actor.Id,
            ActiveFrom = boundary,
            Channel = m_channel.Current.Code,
            ApiTokenId = m_channel.Current.ApiTokenId
        };
        await RevisionVersions.PlaceAsync(m_session, m_outbox, page, previous, revision, false, ct);
        await m_session.SaveAsync(revision, ct);
        m_outbox.PageRevised(page.Id, revision.Id);
        await SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct);
        await CopyAttachmentBindingsAsync(previous, revision, ct);
        if (await m_session.GetAsync<PageCollaboration>(page.Id, ct) is not null)
        {
            await m_checkpoints.AdvanceCheckpointAsync(page, revision, ct);
        }
    }

    /// <summary>
    /// The server's Markdown of the unchanged content (D-L3): a revision stored before the server owned the projection may carry a
    /// client's, which a rename must not copy forward. Content the writer cannot read (a legacy document, whatever it throws) keeps the
    /// stored projection; a projection over the limit is refused like any other write.
    /// </summary>
    private static string Projection(PageRevision previous)
    {
        string markdown;
        try
        {
            markdown = EditorMarkdownWriter.Write(RichDocument.Parse(previous.ContentJson).Document);
        }
        catch (Exception)
        {
            return previous.Markdown;
        }
        Rules.RequireMarkdownSize(markdown);
        return markdown;
    }

    /// <summary>
    /// The content is unchanged, so the new revision carries exactly the previous bindings. Readers are authorized
    /// through these rows once the renamed revision is published. Copying (rather than re-extracting) avoids parsing
    /// the document and keeps bindings to attachments that were closed after the previous revision was saved.
    /// </summary>
    private async Task CopyAttachmentBindingsAsync(PageRevision previous, PageRevision revision, CancellationToken ct)
    {
        var previousId = previous.Id;
        var attachmentIds = await m_session.QueryOver<PageRevisionAttachment>()
            .Where(x => x.RevisionId == previousId)
            .Select(x => x.AttachmentId)
            .ListAsync<Guid>(ct);
        if (attachmentIds.Count == 0)
        {
            return;
        }
        // The revision row must exist before its membership rows; NHibernate does not order these for us.
        await m_session.FlushAsync(ct);
        foreach (var attachmentId in attachmentIds)
        {
            await m_session.SaveAsync(new PageRevisionAttachment { PageId = revision.PageId, RevisionId = revision.Id, AttachmentId = attachmentId }, ct);
        }
        await m_session.FlushAsync(ct);
    }
}
