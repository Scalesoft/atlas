using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.PageLinks;

/// <summary>
/// The page links Markdown written over the API or MCP names, read with the merge's own parser (<see cref="MarkdownToRich"/>) so the read transaction can decide
/// exactly what the merge will ask: <c>[[Název]]</c> titles and the ids of links to Atlas pages. Bounded: titles and ids beyond the
/// caps resolve to nothing, which the merge reports as an unconverted link.
/// </summary>
public static partial class MarkdownLinks
{
    public const int MaxTitles = 100;
    public const int MaxPageIds = 500;

    [GeneratedRegex("^#/pages/(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})")]
    private static partial Regex PageHref();

    public static MarkdownLinkRequest Collect(params string?[] markdown)
    {
        var titles = new List<string>();
        var ids = new List<Guid>();
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<Guid>();
        foreach (var text in markdown)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            foreach (var block in MarkdownToRich.Parse(text).Blocks)
            {
                Walk(block, node =>
                {
                    var type = RichDocument.TypeOf(node);
                    if (type == MarkdownToRich.PageLinkNodeType && node["attrs"]?["title"] is JsonValue title && title.TryGetValue<string>(out var name)
                        && titles.Count < MaxTitles && seenTitles.Add(name))
                    {
                        titles.Add(name);
                    }
                    foreach (var mark in (node["marks"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (RichDocument.TypeOf(mark) == "link" && mark["attrs"]?["href"] is JsonValue value && value.TryGetValue<string>(out var href)
                            && PageHref().Match(href) is { Success: true } page && ids.Count < MaxPageIds && seenIds.Add(Guid.Parse(page.Groups["id"].Value)))
                        {
                            ids.Add(Guid.Parse(page.Groups["id"].Value));
                        }
                    }
                });
            }
        }
        return titles.Count == 0 && ids.Count == 0 ? MarkdownLinkRequest.None : new MarkdownLinkRequest(titles, ids);
    }

    private static void Walk(JsonNode? node, Action<JsonObject> visit)
    {
        switch (node)
        {
            case JsonObject item:
                visit(item);
                if (item["content"] is JsonArray children)
                {
                    foreach (var child in children)
                    {
                        Walk(child, visit);
                    }
                }
                break;
            case JsonArray items:
                foreach (var child in items)
                {
                    Walk(child, visit);
                }
                break;
        }
    }
}

/// <summary>The page links Markdown written over the API or MCP names: <c>[[Název]]</c> titles and ids of <c>atlas://pages/{id}</c> links.</summary>
public sealed record MarkdownLinkRequest(IReadOnlyList<string> Titles, IReadOnlyList<Guid> PageIds)
{
    public static MarkdownLinkRequest None { get; } = new([], []);
}

/// <summary>
/// The answers the merge (K2) asks for, decided in a read transaction on the actor's rights (and G5 over MCP) before the conversion
/// runs: each asked title's resolution and the asked page ids the actor may list. Anything not asked resolves to nothing.
/// </summary>
public sealed record MarkdownLinkTargets(IReadOnlyDictionary<string, PageLinkResolution> Titles, IReadOnlySet<Guid> Pages)
{
    public static MarkdownLinkTargets None { get; } = new(new Dictionary<string, PageLinkResolution>(), new HashSet<Guid>());

    public MergeContext Context(Func<string> newHeadingId)
        => new(title => Titles.GetValueOrDefault(title) ?? PageLinkResolution.None, Pages.Contains, newHeadingId);
}
