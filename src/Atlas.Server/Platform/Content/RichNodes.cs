using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// Builders and small transformations of the editor's rich document JSON, shared by the Markdown conversion
/// (<see cref="MarkdownToRich"/>) and the wiki import.
/// </summary>
public static class RichNodes
{
    public static JsonObject Node(string type, JsonArray? content = null, JsonObject? attributes = null)
    {
        var node = new JsonObject { ["type"] = type };
        if (attributes is not null)
        {
            node["attrs"] = attributes;
        }
        if (content is { Count: > 0 })
        {
            node["content"] = content;
        }
        return node;
    }

    public static JsonObject Mark(string type)
    {
        return new JsonObject { ["type"] = type };
    }

    public static JsonObject LinkMark(string href, string? title = null)
    {
        var attributes = new JsonObject { ["href"] = href, ["target"] = "_blank", ["rel"] = "noopener noreferrer" };
        if (!string.IsNullOrEmpty(title))
        {
            attributes["title"] = title;
        }
        return new JsonObject { ["type"] = "link", ["attrs"] = attributes };
    }

    public static JsonArray Text(string text)
    {
        return text.Length == 0 ? [] : [new JsonObject { ["type"] = "text", ["text"] = text }];
    }

    /// <summary>List items and task items must open with a paragraph.</summary>
    public static JsonArray LeadingParagraph(JsonArray content)
    {
        if (content.Count == 0 || RichDocument.TypeOf(content[0]) != "paragraph")
        {
            content.Insert(0, Node("paragraph"));
        }
        return content;
    }

    /// <summary>Containers (quotes, callouts, cells) require at least one block.</summary>
    public static JsonArray NonEmpty(JsonArray content)
    {
        if (content.Count == 0)
        {
            content.Add(Node("paragraph"));
        }
        return content;
    }

    /// <summary>Moves <paramref name="nodes"/> to the end of <paramref name="target"/>, leaving <paramref name="nodes"/> empty.</summary>
    public static void Append(JsonArray target, JsonArray nodes)
    {
        foreach (var node in nodes.ToArray())
        {
            nodes.Remove(node);
            target.Add(node);
        }
    }

    /// <summary>
    /// Gives <paramref name="node"/> the marks the editor would keep for <paramref name="marks"/> (outermost first): one per
    /// type, the innermost winning, in the order of the editor schema (<see cref="RichDocument.MarkRank"/>), so the stored JSON
    /// is what the editor itself would save.
    /// </summary>
    public static JsonObject WithMarks(JsonObject node, IEnumerable<JsonObject> marks)
    {
        var normalized = new List<JsonObject>();
        // One mark per type; the innermost wins (a nested link replaces an outer one).
        foreach (var mark in marks)
        {
            normalized.RemoveAll(x => RichDocument.TypeOf(x) == RichDocument.TypeOf(mark));
            normalized.Add(mark);
        }
        if (normalized.Count > 0)
        {
            node["marks"] = new JsonArray(normalized.OrderBy(RichDocument.MarkRank).Select(x => (JsonNode)x.DeepClone()).ToArray());
        }
        return node;
    }
}
