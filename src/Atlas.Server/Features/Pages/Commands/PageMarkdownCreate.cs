using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Linking;
using Wolverine;

namespace Atlas.Server.Features.Pages.Commands;

/// <summary>
/// A draft written as Markdown (owner's decision 2026-10-12: API and MCP clients write content only as Markdown, the server converts;
/// the rich document comes only from the web editor). The personal API's create, as MCP's atlas_create_draft: the size first, before
/// anything parses; the create's decisions and the page links of the Markdown in a short read transaction
/// (<see cref="PreparePageMarkdownCreateHandler"/>); the conversion outside any transaction or lock (<see cref="Convert"/>); then the
/// shared <see cref="CreatePageHandler"/> in its own transaction, which decides everything again under the create's locks, validates
/// the result against the editor schema and checks the attachments it references. A token without the right to create there is
/// refused by the read, before the conversion and without the space lock of a create. Not a Wolverine handler: the class name does
/// not end with "Handler".
/// </summary>
public sealed class PageMarkdownCreate(IMessageBus bus)
{
    public async Task<PageDto> CreateAsync(ApiCreatePageRequest api, CancellationToken ct)
    {
        PageMarkdownSaveRunner.RefuseContentJson(api.ContentJson);
        var link = LinkModes.Auto(api.Link);
        var markdown = api.Markdown ?? "";
        PageMarkdownSaveRunner.RequireMarkdown(markdown);
        var request = new CreatePageRequest(api.SpaceId, api.ParentId, api.Title, "", "", api.IsTemplate, api.TemplatePageId);
        var prepared = await bus.InvokeAsync<PageMarkdownCreateBase>(new PreparePageMarkdownCreate(request, MarkdownLinks.Collect(markdown)), ct);
        var (create, warnings) = Convert(request, markdown, prepared.Template, prepared.Links.Context(() => Guid.NewGuid().ToString()));
        var page = await bus.InvokeAsync<PageDto>(create with { Link = link }, ct);
        return page with { Warnings = warnings };
    }

    /// <summary>
    /// The conversion of a Markdown create, between its read and its write and outside both: a new document by the strict parser
    /// (<see cref="RichMarkdownMerge.CreateDocument"/>: no placeholders, password directives refused, every heading a new id), or the
    /// Markdown appended after the template as the actor read it (<see cref="RichMarkdownMerge.Append"/>), which the create copies
    /// with its attachments only when the template is still the same.
    /// </summary>
    public static (CreatePage Create, IReadOnlyList<string> Warnings) Convert(CreatePageRequest request, string markdown, PageDto? template, MergeContext context)
    {
        if (template is null)
        {
            var converted = RichMarkdownMerge.CreateDocument(markdown, context);
            return (new CreatePage(request with { ContentJson = converted.ContentJson, Markdown = converted.Markdown }, FromMarkdown: true), converted.Warnings);
        }
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (new CreatePage(request, FromMarkdown: true), []);
        }
        var document = RichDocument.Parse(template.ContentJson);
        var merged = RichMarkdownMerge.Append(document, RichMarkdownProjection.Project(document, new ProjectionOptions(template.Version)), markdown, context);
        return (new CreatePage(request with { ContentJson = merged.ContentJson, Markdown = merged.Markdown }, FromMarkdown: true, template.ContentJson),
            merged.Warnings);
    }
}

/// <summary>
/// The read before the personal API's Markdown create, in its own transaction: the space FOR SHARE and the actor
/// (<c>LockActorAsync</c>), the create's own refusals and the template (<see cref="CreatePageHandler.PrepareAsync"/>), and the page
/// links of the Markdown resolved on the actor's rights (no G5: that is MCP's).
/// </summary>
public sealed class PreparePageMarkdownCreateHandler(IContentAccess access, CreatePageHandler create, MarkdownLinkResolver links)
{
    public async Task<PageMarkdownCreateBase> Handle(PreparePageMarkdownCreate query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(query.Request.SpaceId, ct);
        var template = await create.PrepareAsync(query.Request, ct);
        return new PageMarkdownCreateBase(template, await links.ResolveAsync(actor, query.Links, null, ct));
    }
}
