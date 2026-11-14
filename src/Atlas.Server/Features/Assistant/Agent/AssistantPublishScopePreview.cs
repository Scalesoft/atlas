using System.Text.Json.Nodes;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Wolverine;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>
/// The server-built preview of the publish-scope writes (spec D4 (3), §6.3): one call of <c>atlas_move_page</c>,
/// <c>atlas_move_page_to_space</c> or <c>atlas_publish</c> is one item. It reads under the user's rights what the tool will need: the
/// page (<see cref="GetMcpLabels"/>: content rights, G5), the source and target trees (<see cref="GetMcpSpaceTree"/>: the web's tree
/// read, G5, the tree version the call expects) and, for a publication, the checks and summary of <see cref="McpPublishSummaries"/>
/// (<see cref="PreviewMcpPublish"/>). What the model can fix (a stale tree, a parent it cannot see, a cycle, an already published
/// version, unsaved live edits) is a tool error within the turn. Whether the user may write into the target is the shared handler's at
/// the confirmation (D19): a refusal there fails the card with its code and nothing moves.
/// </summary>
internal static class AssistantPublishScopePreview
{
    public static Task<AiProposalItemView> ItemAsync(IServiceProvider services, AiProposalShape shape, JsonObject arguments, CancellationToken ct)
    {
        var bus = services.GetRequiredService<IMessageBus>();
        return shape.Kind == AiProposalShape.Publish ? PublishAsync(bus, arguments, ct)
            // A move within the space is a menu of one move: the same checks and the same paths.
            : shape.Tool == "atlas_move_page_to_space" ? MoveToSpaceAsync(bus, arguments, ct)
            : FirstAsync(ArrangeAsync(services, shape, arguments, ct));

        static async Task<AiProposalItemView> FirstAsync(Task<IReadOnlyList<AiProposalItemView>> items) => (await items)[0];
    }

    private static async Task<AiProposalItemView> MoveToSpaceAsync(IMessageBus bus, JsonObject arguments, CancellationToken ct)
    {
        var pageId = McpArguments.RequiredId(AssistantProposals.Text(arguments["pageId"]), "pageId");
        var parentId = McpArguments.OptionalId(AssistantProposals.Text(arguments["parentId"]), "parentId");
        var beforeId = McpArguments.OptionalId(AssistantProposals.Text(arguments["beforePageId"]), "beforePageId");
        var source = (await bus.InvokeAsync<McpLabels>(new GetMcpLabels(pageId), ct)).SpaceId;
        var target = McpArguments.RequiredId(AssistantProposals.Text(arguments["targetSpaceId"]), "targetSpaceId");
        if (target == source)
        {
            throw Invalid("Cílový prostor je prostor stránky; přesun v něm je atlas_move_page.");
        }
        var sourceTree = await TreeAsync(bus, source, arguments["expectedSourceTreeVersion"], ct);
        var targetTree = await TreeAsync(bus, target, arguments["expectedTargetTreeVersion"], ct);
        var page = sourceTree.Items.FirstOrDefault(entry => entry.Item.Id == pageId) ?? throw new ApiException(404, McpVisibility.MissingPage);
        var subpages = Descendants(sourceTree.Items.ToDictionary(entry => entry.Item.Id, entry => entry.Item.ParentId), pageId);
        if (!Flag(arguments["acknowledgeAccessChange"]))
        {
            throw new ApiException(400, "Přesun do jiného prostoru mění přístup: zadej acknowledgeAccessChange: true, jen když to uživatel chce.",
                "access_change_unacknowledged");
        }
        if (arguments["includeSubtree"] is not JsonValue scope || !scope.TryGetValue<bool>(out var includeSubtree))
        {
            throw Invalid("includeSubtree je povinné: true = i podstránky, false = jen stránka.");
        }
        if (!includeSubtree && subpages > 0)
        {
            throw Invalid($"Stránka má {subpages} podstránek; přesuň ji s includeSubtree: true.");
        }
        if (Number(arguments["expectedVersion"]) != page.Item.Version)
        {
            throw McpWriteErrors.VersionConflict(page.Item.Version);
        }
        if (parentId is { } id && targetTree.Items.All(entry => entry.Item.Id != id))
        {
            throw new ApiException(404, "Rodičovská stránka v cílovém prostoru nebyla nalezena.", "not_found");
        }
        if (beforeId is { } before && targetTree.Items.All(entry => entry.Item.Id != before))
        {
            throw new ApiException(404, "Stránka beforePageId v cílovém prostoru nebyla nalezena.", "not_found");
        }
        AiProposalPageRequest[] requests = parentId is { } above ? [new(pageId, source), new(above, target)] : [new(pageId, source), new(Guid.Empty, target)];
        var pages = await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages(requests), ct);
        var title = pages.Items.GetValueOrDefault(pageId)?.Title ?? page.Item.Title ?? "";
        var path = pages.Items.GetValueOrDefault(pageId)?.Path ?? "";
        var where = parentId is { } under && pages.Items.TryGetValue(under, out var placed) ? placed.Path : pages.Spaces.GetValueOrDefault(target) ?? "";
        return new AiProposalItemView(0, pageId, title, path, AiProposalShape.Move, null, null,
            Move: new AiProposalMove(path, where + " › " + title, subpages, true));
    }

    /// <summary>
    /// The rows of atlas_arrange_pages (and the one row of atlas_move_page): one per move, in order, over the space's tree as the user sees it at the version the call
    /// expects. The moves are played on that tree in memory, so a row's "after" is the place once the earlier moves are done, and what
    /// the shared move would refuse at the confirmation (a page or parent outside the tree, a page twice, a cycle, a beforePageId that
    /// is not a sibling under the new parent) is a tool error now, naming the move. Moving into another space is a different tool.
    /// </summary>
    public static async Task<IReadOnlyList<AiProposalItemView>> ArrangeAsync(IServiceProvider services, AiProposalShape shape, JsonObject arguments, CancellationToken ct)
    {
        var bus = services.GetRequiredService<IMessageBus>();
        // atlas_move_page is a menu of one move without moves[]: its errors name neither moves[0] nor "Přesun 1".
        var single = shape.ItemsArgument is null;
        var parsed = McpPageMove.ParseAll(shape.Items(arguments)?.Select(move => new McpPageMoveArgument
        {
            PageId = AssistantProposals.Text(move["pageId"]), ParentId = AssistantProposals.Text(move["parentId"]),
            BeforePageId = AssistantProposals.Text(move["beforePageId"]),
        }).ToArray(), single);
        var space = (await bus.InvokeAsync<McpLabels>(new GetMcpLabels(parsed[0].PageId), ct)).SpaceId;
        var tree = await TreeAsync(bus, space, arguments["expectedTreeVersion"], ct);
        var titles = tree.Items.ToDictionary(entry => entry.Item.Id, entry => entry.Item.Title);
        var parents = tree.Items.ToDictionary(entry => entry.Item.Id, entry => entry.Item.ParentId);
        var spaceName = (await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages([new(Guid.Empty, space)]), ct)).Spaces.GetValueOrDefault(space) ?? "";
        string PathOf(Guid id)
        {
            var names = new List<string>();
            for (Guid? at = id; at is { } current && names.Count <= titles.Count; at = parents.GetValueOrDefault(current))
            {
                // A parent the user may not see is not in the tree: "…", as McpPagePaths writes it.
                names.Insert(0, titles.GetValueOrDefault(current) ?? "…");
                if (!titles.ContainsKey(current))
                {
                    break;
                }
            }
            return string.Join(" › ", names.Prepend(spaceName));
        }
        var items = new AiProposalItemView[parsed.Count];
        for (var i = 0; i < parsed.Count; i++)
        {
            var (page, parent, before) = parsed[i];
            var index = i;
            string Say(string text) => single ? text : $"Přesun {index + 1}: {char.ToLowerInvariant(text[0])}{text[1..]}";
            if (!titles.ContainsKey(page))
            {
                throw new ApiException(404, Say($"Stránka v prostoru {spaceName} není (přesun do jiného prostoru je atlas_move_page_to_space)."), "not_found");
            }
            if (parent is { } above && !titles.ContainsKey(above))
            {
                throw new ApiException(404, Say("Rodičovská stránka v prostoru nebyla nalezena."), "not_found");
            }
            for (var at = parent; at is { } ancestor; at = parents.GetValueOrDefault(ancestor))
            {
                if (ancestor == page)
                {
                    throw Invalid(Say("Stránku nelze přesunout pod ni samotnou ani pod její podstránku."));
                }
            }
            if (before is { } sibling && (sibling == page || !titles.ContainsKey(sibling) || parents.GetValueOrDefault(sibling) != parent))
            {
                throw Invalid(Say("beforePageId musí být jiná stránka pod tímtéž rodičem" + (single ? "." : " (po předchozích přesunech).")));
            }
            var from = PathOf(page);
            var subpages = Descendants(parents, page);
            parents[page] = parent;
            var to = PathOf(page) + (before is { } next ? $" (před {titles[next] ?? "…"})" : "");
            items[i] = new AiProposalItemView(i, page, titles[page] ?? "", from, AiProposalShape.Move, null, null, Move: new AiProposalMove(from, to, subpages, false));
        }
        return items;
    }

    /// <summary>The pages under a page that the user sees; hidden ones move too, but the card can only count what the user may know.</summary>
    private static int Descendants(Dictionary<Guid, Guid?> parents, Guid pageId)
    {
        var count = 0;
        foreach (var id in parents.Keys)
        {
            for (var at = parents[id]; at is { } ancestor; at = parents.GetValueOrDefault(ancestor))
            {
                if (ancestor == pageId)
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static async Task<AiProposalItemView> PublishAsync(IMessageBus bus, JsonObject arguments, CancellationToken ct)
    {
        var pageId = McpArguments.RequiredId(AssistantProposals.Text(arguments["pageId"]), "pageId");
        var version = Number(arguments["expectedVersion"]) is { } value and >= 1 and <= int.MaxValue
            ? (int)value
            : throw Invalid("expectedVersion je povinné: verze z posledního čtení pracovní verze (atlas_get_page s version: \"working\").");
        var changeSummary = AssistantProposals.Text(arguments["changeSummary"]);
        var summary = await bus.InvokeAsync<McpPublishSummary>(new PreviewMcpPublish(pageId, version, changeSummary), ct);
        var pages = await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages([new(pageId, summary.SpaceId)]), ct);
        // A page never published is not among the links the user resolves: its working title in its space.
        var path = pages.Items.GetValueOrDefault(pageId)?.Path ?? summary.SpaceName + " › " + summary.Title;
        return new AiProposalItemView(0, pageId, summary.Title, path, AiProposalShape.Publish, null, null,
            Publish: new AiProposalPublish(summary.Version, summary.PublishedVersion, summary.ChangedSections, changeSummary, summary.Findings));
    }

    /// <summary>The whole tree of a space as the user sees it, at the version the call expects (else 409: re-read the tree).</summary>
    private static async Task<McpSpaceTree> TreeAsync(IMessageBus bus, Guid spaceId, JsonNode? expected, CancellationToken ct)
    {
        if (Number(expected) is not { } version || version < 1)
        {
            throw Invalid("Verze stromu je povinná: treeVersion prostoru z atlas_list_pages.");
        }
        var tree = await bus.InvokeAsync<McpSpaceTree>(new GetMcpSpaceTree(spaceId, 0, int.MaxValue), ct);
        return tree.Version == version
            ? tree
            : throw new ApiException(409, "Strom prostoru se mezitím změnil (treeVersion {version}); načti ho znovu.", "version_conflict", args: new Dictionary<string, object?> { ["version"] = tree.Version });
    }

    private static long? Number(JsonNode? node)
        => node is JsonValue value && (value.TryGetValue<long>(out var number) || value.TryGetValue<string>(out var text) && long.TryParse(text, out number)) ? number : null;

    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static ApiException Invalid(string message) => new(400, message, "invalid_argument");
}
