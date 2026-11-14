using Atlas.Server.Features.Pages.Contracts;

namespace Atlas.Server.Features.Pages.Commands;

/// <param name="FromMarkdown">As <see cref="SavePage.FromMarkdown"/>: no attachment can belong to a page that does not exist yet.</param>
/// <param name="TemplateContentJson">
/// From a template with Markdown appended outside the transaction (<see cref="PageMarkdownCreate"/>): the template's content as the
/// actor read it, and <c>Request.ContentJson</c> is that content with the Markdown merged after it. A template changed since is 409
/// <c>version_conflict</c>; the copy's attachments are remapped in the merged content.
/// </param>
/// <param name="CopyAttachmentsOf">A page the content was copied from (Projector's deployment copy): its attachments are copied to the
/// new page and remapped, as a template's are.</param>
/// <param name="Link">As <see cref="SavePage.Link"/>: with a template only the appended Markdown is linked.</param>
public sealed record CreatePage(CreatePageRequest Request, bool FromMarkdown = false, string? TemplateContentJson = null, Guid? CopyAttachmentsOf = null,
    bool Link = true);
