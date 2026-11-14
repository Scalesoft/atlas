using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Linking;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Commands;

/// <summary>
/// The personal API's save: the whole working revision as Markdown with the placeholders of <c>GET /api/v1/pages/{id}?format=markdown</c>,
/// merged into the stored revision as MCP's atlas_replace_page does (<see cref="RichMarkdownMerge.ReplaceAll"/>: placeholders bring
/// their nodes back, unchanged blocks keep their stored JSON, a placeholder left out removes its component only when
/// <c>removeComponents</c> lists it). As over MCP, the base is read in its own transaction (<see cref="PreparePageMarkdownSaveHandler"/>),
/// the conversion holds no lock, and the shared <see cref="SavePageHandler"/> saves in another with the expected version (and the
/// attachment check of Markdown, <c>AttachmentReferences.RequireAddedAsync</c>), so a change in between is a version conflict and
/// nothing is written. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
public sealed class PageMarkdownSaveRunner(IMessageBus bus)
{
    public async Task<PageDto> SaveAsync(Guid pageId, ApiSavePageRequest request, CancellationToken ct)
    {
        RefuseContentJson(request.ContentJson);
        var link = LinkModes.Auto(request.Link);
        var markdown = request.Markdown ?? throw new ApiException(400,
            "markdown je povinné: celý text stránky z GET /api/v1/pages/{id}?format=markdown se značkami ⟦c:N⟧.", "invalid_markdown");
        // Before anything parses the text or reads the page: the parse and the merge cost time in proportion to these.
        RequireMarkdown(markdown);
        RequireRemovals(request.RemoveComponents);
        var edit = await bus.InvokeAsync<PageMarkdownBase>(new PreparePageMarkdownSave(pageId, request.ExpectedVersion, MarkdownLinks.Collect(markdown)), ct);
        RichDocument document;
        try
        {
            document = RichDocument.Parse(edit.ContentJson);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(409, "Stránka nemá obsah editoru; otevřete ji v editoru Atlasu a upravte, uloží se sama.", "invalid_content");
        }
        // Between the transactions: the conversion holds no lock; the save below checks the version again.
        var merged = RichMarkdownMerge.ReplaceAll(document, RichMarkdownProjection.Project(document, new ProjectionOptions(edit.Version)), markdown,
            edit.Links.Context(() => Guid.NewGuid().ToString()), request.RemoveComponents, request.ResetFormatting);
        var saved = await bus.InvokeAsync<PageDto>(new SavePage(pageId, new SavePageRequest(request.Title ?? edit.Title, merged.ContentJson, merged.Markdown,
            request.ExpectedVersion), FromMarkdown: true, link), ct);
        return saved with { Warnings = merged.Warnings };
    }

    /// <summary>At most this many component numbers in <c>removeComponents</c> (REST and atlas_replace_page), refused before the merge.</summary>
    public const int MaxRemoveComponents = 1000;

    /// <summary>The Markdown limit of the merge, checked by every Markdown write before anything parses the text (S-M1).</summary>
    internal static void RequireMarkdown(string? markdown)
    {
        if (markdown is { Length: > Rules.MaxDocumentMarkdownLength })
        {
            throw new ApiException(413, "Markdown je příliš dlouhý: nejvýše 1 000 000 znaků. Upravte stránku po sekcích.", "invalid_markdown");
        }
    }

    internal static void RequireRemovals(IReadOnlyCollection<int>? removeComponents)
    {
        if (removeComponents is { Count: > MaxRemoveComponents })
        {
            throw new ApiException(400, "removeComponents uvádí nejvýše {maxRemoveComponents} čísel značek ⟦c:N⟧.", "invalid_argument", args: new Dictionary<string, object?> { ["maxRemoveComponents"] = MaxRemoveComponents });
        }
    }

    /// <summary>The rich document is the web editor's alone (owner's decision 2026-10-12); a token sends Markdown.</summary>
    internal static void RefuseContentJson(string? contentJson)
    {
        if (!string.IsNullOrEmpty(contentJson))
        {
            throw new ApiException(400, "Osobní API nepřijímá contentJson. Pošlete obsah jako markdown; komponenty zůstanou zachované přes značky ⟦c:N⟧ " +
                "z GET /api/v1/pages/{id}?format=markdown.", "content_json_not_accepted");
        }
    }
}

/// <summary>
/// The read before the personal API's Markdown save, in its own transaction: the page through the gate with EditContent, the expected
/// version, the stored working revision as the reader's copy the client projected (a copy without password references the actor may
/// not see cannot be merged back: 403 <c>hidden_secret_references</c>, which the save checks again), and the page links of the
/// Markdown resolved on the actor's rights (no G5: that is MCP's).
/// </summary>
public sealed class PreparePageMarkdownSaveHandler(ISession session, PageGate gate, SecretReferenceAccess references, MarkdownLinkResolver links)
{
    public async Task<PageMarkdownBase> Handle(PreparePageMarkdownSave query, CancellationToken ct)
    {
        var edited = (await gate.EditAsync(query.PageId, AccessOperation.EditContent, ct)).Require();
        var page = edited.Page;
        Rules.ExpectVersion(page.Version, query.ExpectedVersion);
        var revision = await session.QueryOver<PageRevision>()
            .Where(x => x.PageId == page.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        var (copy, hidden) = await references.ForReaderAsync(edited.Actor, revision.ContentJson, ct);
        if (hidden)
        {
            await references.RequireEditableAsync(edited.Actor, revision.ContentJson, ct);
        }
        var targets = await links.ResolveAsync(edited.Actor, query.Links, null, ct);
        return new PageMarkdownBase(revision.Title, copy, page.Version, targets);
    }
}
