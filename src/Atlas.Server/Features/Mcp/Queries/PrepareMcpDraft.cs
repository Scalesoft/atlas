using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Pages.Contracts;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>
/// Step 1 of atlas_create_draft: G5 of the target space, the create's own decisions with the template, and the page links of the new
/// Markdown, before the conversion.
/// </summary>
public sealed record PrepareMcpDraft(CreatePageRequest Request, MarkdownLinkRequest Links);
