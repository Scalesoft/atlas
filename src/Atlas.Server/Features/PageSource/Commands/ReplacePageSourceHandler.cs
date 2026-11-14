using Atlas.Server.Features.Vaults.References;
using NHibernate;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.PageSource.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Features.Search;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageSource.Commands;

/// <summary>
/// Replaces a page from the source editor like a restore from history: a new working revision, a collaboration
/// reset to a new generation and a "page.source-edited" audit fact. ManageAccess on the page (space Admin, page manager or WikiAdmin). Refused while
/// other users have unsaved live edits; the client saves its own before it opens the source and before it applies.
/// </summary>
public sealed class ReplacePageSourceHandler
{
    private readonly ISession m_session;
    private readonly PageGate m_gate;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly PageReader m_reader;
    private readonly SecretReferenceAccess m_references;
    private readonly IRequestChannel m_channel;
    private readonly Outbox m_outbox;

    private readonly RequirementPageConversion m_conversion;

    public ReplacePageSourceHandler(ISession session, PageGate gate, CollaborationCheckpoints checkpoints, PageReader reader,
        SecretReferenceAccess references, IRequestChannel channel, Outbox outbox, RequirementPageConversion conversion)
    {
        m_conversion = conversion;
        m_outbox = outbox;
        m_channel = channel;
        m_references = references;
        m_session = session;
        m_gate = gate;
        m_checkpoints = checkpoints;
        m_reader = reader;
    }

    public async Task<PageDto> Handle(ReplacePageSource command, CancellationToken ct)
    {
        var request = command.Request;
        var (referenced, markdown) = Rules.ValidateDocument(request.ContentJson);
        // The gate takes a page the session already holds as it is under the lock, not as it was loaded. Strangers and
        // inaccessible drafts learn nothing about the page; anyone who can read it is told why it is refused.
        var edited = (await m_gate.EditAsync(command.Id, AccessOperation.ManageAccess, ct))
            .Require("Zdroj stránky může upravit pouze správce prostoru nebo správce stránky.");
        var (page, actor) = (edited.Page, edited.Actor);
        Rules.ExpectVersion(page.Version, request.ExpectedVersion);
        // Requirement blocks (or a version-3 party) are written as requirement headings, as by any write.
        if (await m_conversion.ConvertWriteAsync(page.Id, page.SpaceId, request.ContentJson, ct) is var shaped && shaped != request.ContentJson)
        {
            request = request with { ContentJson = shaped };
            (referenced, markdown) = Rules.ValidateDocument(request.ContentJson);
        }
        var channel = m_channel.Current;
        await m_checkpoints.RequireNoForeignUnsavedAsync(page.Id, actor.Id, ct, includeOwn: channel.RefusesOwnUnsaved);
        var current = await m_session.QueryOver<PageRevision>()
            .Where(x => x.PageId == command.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct)
            ?? throw new ApiException(409, "Aktuální verze stránky není dostupná.");
        // WP4 V6: as a save, never over hidden references, and only with references the actor may use.
        await m_references.RequireEditableAsync(actor, current.ContentJson, ct);
        await m_references.RequireUsableAsync(actor, SecretReferenceAccess.Added(referenced, current.ContentJson), ct);
        if (current.ContentJson == request.ContentJson && current.Markdown == markdown)
        {
            await m_checkpoints.ResetAsync(page, current, ct);
            return await m_reader.ReadEditedAsync(edited, ct);
        }
        if (page.Version == int.MaxValue)
        {
            throw new ApiException(409, "Byl dosažen limit verzí stránky.");
        }
        var boundary = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
        current.ActiveTo = boundary;
        await m_session.FlushAsync(ct);
        page.Version++;
        var replaced = new PageRevision
        {
            Id = Guid.NewGuid(),
            PageId = command.Id,
            Version = page.Version,
            AuthorId = actor.Id,
            Title = current.Title,
            ContentJson = request.ContentJson,
            Markdown = markdown,
            ActiveFrom = boundary,
            Channel = channel.Code,
            ApiTokenId = channel.ApiTokenId
        };
        await RevisionVersions.PlaceAsync(m_session, m_outbox, page, current, replaced, true, ct);
        await m_session.SaveAsync(replaced, ct);
        m_outbox.PageRevised(page.Id, replaced.Id);
        await SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct);
        await AttachmentReferences.BindRevisionAsync(m_session, replaced, ct);
        await m_checkpoints.ResetAsync(page, replaced, ct);
        await m_session.SaveAsync(new PageSourceEditEvent { PageId = page.Id, RevisionId = replaced.Id, ActorId = actor.Id }, ct);
        // Keep PublishedRevisionId and PagePublication untouched: like a restore, this creates a working revision.
        return await m_reader.ReadEditedAsync(edited, ct);
    }
}
