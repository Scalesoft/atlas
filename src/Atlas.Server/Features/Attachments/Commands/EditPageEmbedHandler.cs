using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Attachments.Commands;

/// <summary>
/// An embed change in the working revision (REST PATCH /pages/{id}/embeds/{attachmentId}, MCP atlas_update_embed): the page
/// through the gate FOR UPDATE with EditContent, the expected version, the stored revision read under that lock, the replacement
/// checked there (active, of this very page, an image for an image), the change made by <see cref="RichMarkdownMerge"/> on the stored
/// rich document with its invariant guard, and the result saved through the shared <see cref="SavePageHandler"/> in the same
/// transaction: CAS again, the rule against unsaved live edits, password references, the revision's attachment bindings, the
/// channel and the token. Nothing is published; readers keep the published revision.
/// </summary>
public sealed class EditPageEmbedHandler
{
    private readonly ISession m_session;
    private readonly PageGate m_gate;
    private readonly SavePageHandler m_save;

    public EditPageEmbedHandler(ISession session, PageGate gate, SavePageHandler save)
    {
        m_session = session;
        m_gate = gate;
        m_save = save;
    }

    public Task<PageDto> Handle(EditPageEmbed command, CancellationToken ct)
    {
        return ApplyAsync(command.Edit, ct);
    }

    /// <summary>The change inside the caller's transaction (also MCP's own command, which adds G5 first).</summary>
    public async Task<PageDto> ApplyAsync(UpdatePageEmbed edit, CancellationToken ct)
    {
        if (edit.ExpectedVersion < 1)
        {
            throw new ApiException(400, "expectedVersion je povinné: verze z posledního čtení stránky.", "invalid_argument");
        }
        RequireArguments(edit);
        // The save opens the page through the gate again: the same locks, already held by this transaction.
        var page = (await m_gate.EditAsync(edit.PageId, AccessOperation.EditContent, ct)).Require().Page;
        Rules.ExpectVersion(page.Version, edit.ExpectedVersion);
        var revision = await m_session.QueryOver<PageRevision>()
            .Where(x => x.PageId == page.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        RichDocument document;
        try
        {
            document = RichDocument.Parse(revision.ContentJson);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(409, "Stránka nemá obsah editoru; otevřete ji v editoru Atlasu a upravte, uloží se sama.", "invalid_content");
        }
        var merged = RichMarkdownMerge.UpdateEmbed(document, edit.AttachmentId, edit.Occurrence, edit.Look,
            edit.ReplacementId is { } replacement ? await AttachmentAsync(page.Id, replacement, ct) : null);
        return await m_save.Handle(new SavePage(page.Id, new SavePageRequest(revision.Title, merged.ContentJson, null, edit.ExpectedVersion)), ct);
    }

    /// <summary>The arguments alone, before any lock: an invalid one costs no page read.</summary>
    public static void RequireArguments(UpdatePageEmbed edit)
    {
        RequireId(edit.AttachmentId);
        if (edit.ReplacementId is { } replacement)
        {
            RequireId(replacement);
        }
        if (edit.Look.IsEmpty && edit.ReplacementId is null)
        {
            throw new ApiException(400, "Zadejte, co se má změnit: alternativní text, šířku, zarovnání nebo jinou přílohu.", "invalid_argument");
        }
        if (edit.Occurrence is < 1)
        {
            throw new ApiException(400, "occurrence je pořadí vložení přílohy na stránce od 1.", "invalid_argument");
        }
    }

    /// <summary>An active attachment of this page; any other id (unknown, removed, of another page) is the same 404.</summary>
    private async Task<EmbedAttachment> AttachmentAsync(Guid pageId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await m_session.GetAsync<Attachment>(attachmentId, ct);
        if (attachment is null || attachment.PageId != pageId || attachment.ActiveTo is not null)
        {
            throw new ApiException(404, "Příloha u této stránky není nebo už není dostupná. Nahrajte ji ke stránce.", "attachment_not_found");
        }
        return new EmbedAttachment(attachment.Id, attachment.Name, attachment.Size, attachment.MimeType, attachment.IsImage);
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ApiException(400, "attachmentId je povinné (UUID přílohy stránky).", "invalid_argument");
        }
    }
}
