using System.Collections.Concurrent;
using System.Text.Json;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>A page a tool returned in this turn: <see cref="Read"/> when <c>atlas_get_page</c> read it, otherwise <c>atlas_search</c> found it.</summary>
public sealed record AssistantReturnedPage(Guid PageId, string Title, bool Read);

/// <summary>
/// What the tools of one turn returned, read from their structured results before the output cap (<see cref="AssistantToolSession"/>):
/// the pages that may become the answer's sources (only <c>atlas_search</c> hits, the pages of <c>projector_query</c> and <c>projector_waiting</c>
/// rows, and pages read with <c>atlas_get_page</c> in their published version, spec §4.1; a page read only in its working version, such as a
/// never-published draft, is no source and no dependency)
/// and the display names of every page and space any result named, for the step's target. Never text of a page; nothing is logged.
/// </summary>
public sealed class AssistantToolResults
{
    /// <summary>The longest step target (a page title or space name) the stream carries.</summary>
    public const int TargetLength = 80;

    private readonly ConcurrentQueue<AssistantReturnedPage> m_pages = new();
    private readonly ConcurrentDictionary<Guid, string> m_pageTitles = new();
    private readonly ConcurrentDictionary<Guid, string> m_spaceNames = new();

    /// <summary>The source candidates in the order the tools returned them (a page may repeat).</summary>
    public IReadOnlyList<AssistantReturnedPage> Pages => m_pages.ToArray();

    public void Observe(string tool, JsonElement? structured)
    {
        if (structured is not { ValueKind: JsonValueKind.Object } root)
        {
            return;
        }
        Names(root);
        if (tool == "atlas_get_page" && Page(root) is { } read)
        {
            // A working version is the editor's unpublished text, never a citable published source (spec §7.1): no source, no dependency.
            if (Text(root, "version") != "working")
            {
                m_pages.Enqueue(read with { Read = true });
            }
        }
        else if (tool == "atlas_search" && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in items.EnumerateArray())
            {
                if (Page(hit) is { } found)
                {
                    m_pages.Enqueue(found);
                }
            }
        }
        else if (tool is "projector_query" or "projector_waiting")
        {
            // Plan 3.5 I: the zakázka pages these list (rows with pageId and pageTitle) may be cited like search hits, so an answer
            // from them links its pages; only a published revision becomes a source (ReleaseAgentAnswer).
            RequirementPages(root);
        }
    }

    private void RequirementPages(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && Guid.TryParse(Text(element, "pageId"), out var id) && Text(element, "pageTitle") is { Length: > 0 } title)
        {
            m_pages.Enqueue(new AssistantReturnedPage(id, title, false));
            return;
        }
        if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            foreach (var child in element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().Select(x => x.Value) : element.EnumerateArray())
            {
                RequirementPages(child);
            }
        }
    }

    /// <summary>The display name a call concerns: the title of its <c>pageId</c>, else the name of its <c>spaceId</c>, when a result of
    /// this turn named it; null otherwise. Never an argument value itself (a search text stays out of the stream and the store).</summary>
    public string? Target(IReadOnlyDictionary<string, object?> arguments)
    {
        var name = Id(arguments, "pageId") is { } page && m_pageTitles.TryGetValue(page, out var title) ? title
            : Id(arguments, "spaceId") is { } space && m_spaceNames.TryGetValue(space, out var spaceName) ? spaceName
            : null;
        return name is null || name.Length <= TargetLength ? name : name[..(TargetLength - 1)].TrimEnd() + "…";
    }

    private static Guid? Id(IReadOnlyDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) && (value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null,
        }) is { } id && Guid.TryParse(id, out var guid) ? guid : null;

    private static AssistantReturnedPage? Page(JsonElement item)
        => item.ValueKind == JsonValueKind.Object && Guid.TryParse(Text(item, "pageId"), out var id) && Text(item, "title") is { Length: > 0 } title
            ? new AssistantReturnedPage(id, title, false)
            : null;

    /// <summary>Every object of the result with <c>pageId</c> + <c>title</c> or <c>spaceId</c> + <c>spaceName</c>/<c>name</c>.</summary>
    private void Names(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (Page(element) is { } page)
                {
                    m_pageTitles[page.PageId] = page.Title;
                }
                if (Guid.TryParse(Text(element, "spaceId"), out var space) && (Text(element, "spaceName") ?? Text(element, "name")) is { Length: > 0 } name)
                {
                    m_spaceNames[space] = name;
                }
                foreach (var property in element.EnumerateObject())
                {
                    Names(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Names(item);
                }
                break;
        }
    }

    private static string? Text(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
