using Atlas.Markdown.Sync;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.PageHistory.Domain;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Features.Search;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageHistory.Commands;

public sealed class RestorePageHandler
{
    private readonly ISession m_session;
    private readonly PageHistoryAccess m_pages;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly PageReader m_reader;
    private readonly SecretReferenceAccess m_references;
    private readonly IRequestChannel m_channel;
    private readonly Outbox m_outbox;
    private readonly McpOrganizationGuard m_mcp;
    private readonly RequirementPageConversion m_conversion;

    public RestorePageHandler(ISession session, PageHistoryAccess pages, CollaborationCheckpoints checkpoints, PageReader reader,
        SecretReferenceAccess references, IRequestChannel channel, Outbox outbox, McpOrganizationGuard mcp, RequirementPageConversion conversion)
    {
        m_conversion = conversion;
        m_mcp = mcp;
        m_outbox = outbox;
        m_channel = channel;
        m_references = references;
        m_session = session;
        m_pages = pages;
        m_checkpoints = checkpoints;
        m_reader = reader;
    }

    public async Task<PageDto> Handle(RestorePage command, CancellationToken ct)
    {
        // G5 on MCP and the assistant (atlas_restore_version) under the space lock the save keeps: a space out of AI is a missing page.
        if (m_channel.Current.IsAiBound)
        {
            await m_mcp.LockPageSpaceSharedAsync(command.Id, ct);
        }
        var (page, actor) = await m_pages.RequirePageAsync(command.Id, true, ct);
        Rules.ExpectVersion(page.Version, command.Request.ExpectedVersion);
        var source = await m_pages.FindRevisionAsync(command.Id, command.Request.RevisionId, ct);
        // The restore resets the generation like a save without one; the restorer already confirmed discarding only their own edits.
        // Over mcp nobody confirmed anything, so any unsaved edits refuse (D3).
        var channel = m_channel.Current;
        await m_checkpoints.RequireNoForeignUnsavedAsync(page.Id, actor.Id, ct, includeOwn: channel.RefusesOwnUnsaved);
        var current = await m_session.QueryOver<PageRevision>()
            .Where(x => x.PageId == command.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct)
            ?? throw new ApiException(409, "Aktuální verze stránky není dostupná.");
        // WP4 V6: a restore replaces the working revision like a save; the restored revision may bring back only references the
        // actor may Discover (the history preview showed it without the others).
        await m_references.RequireEditableAsync(actor, current.ContentJson, ct);
        await m_references.RequireEditableAsync(actor, source.ContentJson, ct);
        // A restore writes the old content again, so it meets the editor schema like any other write: a revision stored before
        // the server validated it (or by an older editor) is not brought back as a working revision the editor cannot open.
        // Its Markdown is written again from the JSON: a revision stored before the server owned the projection may carry a client's.
        // Shapes Atlas 2.x stored (a link in a link, a list item without its paragraph) come back repaired, as the editor shows them.
        // A revision from before Atlas 3.6 brings its requirement blocks back as requirement headings.
        var content = await m_conversion.ConvertWriteAsync(page.Id, page.SpaceId, LegacyDocumentRepair.Repair(source.ContentJson), ct,
            "Tuto verzi z historie nejde obnovit: její požadavky v původním tvaru (bloky) nejde beze ztráty převést na nadpisy požadavků. " +
            "Zkopírujte z ní, co potřebujete, do současné verze.");
        var markdown = Rules.ValidateDocument(content).Markdown;
        if (current.Title == source.Title && current.ContentJson == content && current.Markdown == markdown)
        {
            await m_checkpoints.ResetAsync(page, current, ct);
            return await m_reader.ReadAsync(command.Id, ct);
        }
        if (page.Version == int.MaxValue)
        {
            throw new ApiException(409, "Byl dosažen limit verzí stránky.");
        }
        var boundary = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
        current.ActiveTo = boundary;
        await m_session.FlushAsync(ct);
        page.Version++;
        var restored = new PageRevision
        {
            Id = Guid.NewGuid(),
            PageId = command.Id,
            Version = page.Version,
            AuthorId = actor.Id,
            Title = source.Title,
            ContentJson = content,
            Markdown = markdown,
            ActiveFrom = boundary,
            Channel = channel.Code,
            ApiTokenId = channel.ApiTokenId
        };
        await RevisionVersions.PlaceAsync(m_session, m_outbox, page, current, restored, true, ct);
        await m_session.SaveAsync(restored, ct);
        m_outbox.PageRevised(page.Id, restored.Id);
        await SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct);
        await AttachmentReferences.BindRevisionAsync(m_session, restored, ct);
        await m_checkpoints.ResetAsync(page, restored, ct);
        // A restore creates a working revision and leaves the publication alone, except „Zahodit změny“: the restored title and document are
        // exactly what readers read, so the publication carries over like a save that returned to it (SavePageHandler) and nothing waits
        // for Publikovat. shortcut: a published revision the restore had to repair or convert stays a draft, the user publishes it.
        if (page.PublishedRevisionId is { } publishedId && await m_session.GetAsync<PageRevision>(publishedId, ct) is { } published
            && published.Title == restored.Title && published.ContentJson == restored.ContentJson)
        {
            await PagePublications.CarryOverAsync(m_session, m_outbox, page, restored.Id, actor.Id, ct);
        }
        return await m_reader.ReadAsync(command.Id, ct);
    }
}
