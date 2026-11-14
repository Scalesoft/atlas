using Atlas.Server.Features.Pages.Contracts;

namespace Atlas.Server.Features.Pages.Commands;

/// <param name="FromMarkdown">The content was converted from Markdown of the API or MCP: the attachments it adds must be the page's
/// own, active, and images where it shows them as images (<c>AttachmentReferences.RequireAddedAsync</c>).</param>
/// <param name="Link">With <paramref name="FromMarkdown"/>: the blocks the write changed are linked (<c>link: "auto"</c>, EntityLinker);
/// false is <c>link: "off"</c>.</param>
public sealed record SavePage(Guid Id, SavePageRequest Request, bool FromMarkdown = false, bool Link = true);
