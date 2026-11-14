using System.ComponentModel;
using Atlas.Server.Features.PageOrganization.Contracts;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>atlas_move_page: <see cref="MovePageRequest"/> (parent, position, tree version) through the shared move, with G5 under its locks.</summary>
public sealed record MoveMcpPage(Guid PageId, MovePageRequest Request);

/// <summary>atlas_arrange_pages: several <see cref="MovePageRequest"/>s in one space, one after another in one transaction, each
/// expecting the tree version the previous one left (the first the caller's); the first refusal undoes them all.</summary>
public sealed record ArrangeMcpPages(IReadOnlyList<McpPageMove> Moves, long ExpectedTreeVersion);

/// <summary>One move of <see cref="ArrangeMcpPages"/>: the page, its new parent (null = root) and the sibling it goes before (null = last).</summary>
public sealed record McpPageMove(Guid PageId, Guid? ParentId, Guid? BeforePageId)
{
    /// <summary>The most items of one MCP batch call (moves, labels) and of one assistant proposal card.</summary>
    public const int MaxMoves = 100;

    /// <summary>The moves as atlas_arrange_pages and the assistant's preview of it read them: 1 to <see cref="MaxMoves"/>, every
    /// page once. <paramref name="single"/>: the one move of atlas_move_page, whose arguments are named as they are, without moves[i].</summary>
    /// <exception cref="ApiException">400 invalid_argument naming the move.</exception>
    public static IReadOnlyList<McpPageMove> ParseAll(IReadOnlyList<McpPageMoveArgument?>? moves, bool single = false)
    {
        if (moves is not { Count: >= 1 and <= MaxMoves })
        {
            throw new ApiException(400, $"moves musí mít 1 až {MaxMoves} položek.", "invalid_argument");
        }
        var parsed = new List<McpPageMove>(moves.Count);
        for (var index = 0; index < moves.Count; index++)
        {
            var move = moves[index] ?? new McpPageMoveArgument();
            var item = single ? "" : $"moves[{index}].";
            var id = McpArguments.RequiredId(move.PageId, item + "pageId");
            if (parsed.Any(x => x.PageId == id))
            {
                throw new ApiException(400, $"moves[{index}]: stránka už je v moves; každou stránku přesuňte jen jednou, rovnou na cílové místo.", "invalid_argument");
            }
            parsed.Add(new McpPageMove(id, McpArguments.OptionalId(move.ParentId, item + "parentId"), McpArguments.OptionalId(move.BeforePageId, item + "beforePageId")));
        }
        return parsed;
    }
}

/// <summary>One item of atlas_arrange_pages as the model sends it; parsed and checked by <see cref="McpPageMove.ParseAll"/>.</summary>
public sealed class McpPageMoveArgument
{
    [Description("pageId přesouvané stránky; každá stránka nejvýše jednou.")]
    public string? PageId { get; init; }

    [Description("pageId nové rodičovské stránky ve stejném prostoru; bez něj kořen prostoru.")]
    public string? ParentId { get; init; }

    [Description("pageId sourozence pod tímtéž rodičem, před kterého se stránka zařadí; bez něj na konec úrovně.")]
    public string? BeforePageId { get; init; }
}

/// <summary>atlas_sort_pages: <see cref="SortTreeLevelRequest"/> („Řadit abecedně“ of one level) through the shared handler, with G5 under its locks.</summary>
public sealed record SortMcpTreeLevel(Guid SpaceId, SortTreeLevelRequest Request);

/// <summary>atlas_move_page_to_space:<see cref="MovePageSpaceRequest"/> through the shared cross-space move; both spaces must take part in MCP.</summary>
public sealed record MoveMcpPageToSpace(Guid PageId, MovePageSpaceRequest Request);

/// <summary>atlas_trash_page: <see cref="TrashPageRequest"/> through the shared trash.</summary>
public sealed record TrashMcpPage(Guid PageId, TrashPageRequest Request);

/// <summary>atlas_restore_page: <see cref="UntrashPageRequest"/> through the shared restore (space Admin).</summary>
public sealed record RestoreMcpPage(Guid PageId, UntrashPageRequest Request);

/// <summary>atlas_list_trash: the shared trash listing (space Admin) of a space MCP may see.</summary>
public sealed record ListMcpTrash(Guid SpaceId, int Offset, int Limit);

/// <summary>An organization write done within one space: the space it happened in and the shared handler's change.</summary>
public sealed record McpOrganizationResult(Guid SpaceId, OrganizationChange Change);
