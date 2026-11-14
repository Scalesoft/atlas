using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Contracts;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>
/// atlas_create_draft: <see cref="CreatePageRequest"/> through the shared create, converted before this transaction. From a template
/// with appended text, <paramref name="TemplateContentJson"/> is the template it was appended to (<see cref="CreatePage.TemplateContentJson"/>).
/// </summary>
public sealed record CreateMcpDraft(CreatePageRequest Request, string? TemplateContentJson = null, bool Link = true);

/// <summary>atlas_update_section, atlas_insert_section, atlas_replace_page: the merged working revision through the shared save.</summary>
public sealed record SaveMcpDraft(Guid PageId, SavePageRequest Request, bool Link = true);

/// <summary>atlas_rename_page: the title through the shared rename.</summary>
public sealed record RenameMcpPage(Guid PageId, RenamePageRequest Request);
