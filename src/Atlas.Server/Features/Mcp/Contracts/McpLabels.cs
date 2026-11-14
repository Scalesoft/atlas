using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageTags;

namespace Atlas.Server.Features.Mcp.Contracts;

/// <summary>atlas_get_labels: the icon, colour and tags of a page the actor may read in a space MCP may see.</summary>
public sealed record McpLabels(Guid PageId, Guid SpaceId, PageDecorationDto? Decoration, IReadOnlyList<TagDto> Tags);

/// <summary>One item of atlas_apply_labels as the model sends it; parsed and checked by the tool. A part left out stays as it is.</summary>
public sealed class McpLabelChangeArgument
{
    [System.ComponentModel.Description("pageId stránky; každá stránka nejvýše jednou.")]
    public string? PageId { get; init; }

    [System.ComponentModel.Description("Id ikony z atlas_list_appearance, například book-open. Spolu s color.")]
    public string? Icon { get; init; }

    [System.ComponentModel.Description("Id barvy z atlas_list_appearance, například blue-3. Spolu s icon.")]
    public string? Color { get; init; }

    [System.ComponentModel.Description("true odstraní ikonu i barvu (icon a color pak vynechte).")]
    public bool ClearDecoration { get; init; }

    [System.ComponentModel.Description("Tagy k přidání (názvy); ostatní tagy stránky zůstanou.")]
    public string[]? AddTags { get; init; }

    [System.ComponentModel.Description("Tagy k odebrání (názvy); tag, který stránka nemá, se přeskočí.")]
    public string[]? RemoveTags { get; init; }

    [System.ComponentModel.Description("revisionId ikony z atlas_get_labels, atlas_get_space_tree nebo atlas_create_draft; vynechte, jen když stránka ikonu opravdu nemá. Jen se změnou ikony.")]
    public string? ExpectedRevisionId { get; init; }

    [System.ComponentModel.Description("id tagů stránky z posledního čtení (volitelné); jiná sada je chyba tags_changed. Jen se změnou tagů.")]
    public string[]? ExpectedTagIds { get; init; }
}
