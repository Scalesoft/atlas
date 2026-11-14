using System.Text.Json.Nodes;

namespace Atlas.Server.Tests.Projector;

/// <summary>Page content with requirement blocks in the node shape of the Projector v1 spec §2, built directly (R1 owns the editor).</summary>
internal static class ProjectorDocuments
{
    /// <summary>A document that names the plugins it uses, as the editor saves one: heading anchors, and the requirement blocks of R1
    /// when it holds any.</summary>
    public static JsonObject Rich(params JsonNode[] content)
    {
        var document = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(content) };
        var plugins = new JsonArray(new JsonObject { ["id"] = "heading-anchors", ["version"] = 1 });
        if (document.ToJsonString().Contains("\"requirement\"", StringComparison.Ordinal))
        {
            plugins.Add(new JsonObject { ["id"] = "atlas-requirements", ["version"] = 1 });
        }
        else if (document.ToJsonString().Contains("\"direction\"", StringComparison.Ordinal))
        {
            // Requirement headings (shape 2026-11-04) need the plugin in its version 4 to pass the save's schema check.
            plugins.Add(new JsonObject { ["id"] = "atlas-requirements", ["version"] = 4 });
        }
        if (document.ToJsonString().Contains("\"pageReference\"", StringComparison.Ordinal))
        {
            plugins.Add(new JsonObject { ["id"] = "pages", ["version"] = 1 });
        }
        return new JsonObject { ["format"] = "atlas-rich", ["version"] = 1, ["plugins"] = plugins, ["document"] = document };
    }

    public static string Json(params JsonNode[] content) => Rich(content).ToJsonString();

    public static JsonObject Block(string id, string key, string title, string state = "planned", string direction = "internal", string requires = "",
        string date = "", string party = "", string where = "", params JsonNode[] content)
    {
        return new JsonObject
        {
            ["type"] = "requirement",
            ["attrs"] = new JsonObject
            {
                ["id"] = id, ["key"] = key, ["direction"] = direction, ["state"] = state, ["date"] = date, ["party"] = party, ["where"] = where,
                ["requires"] = requires
            },
            ["content"] = new JsonArray([Paragraph(title), .. content])
        };
    }

    public static JsonObject Paragraph(string text)
    {
        return text.Length == 0
            ? new JsonObject { ["type"] = "paragraph" }
            : new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
    }

    public static JsonObject Heading(string text, string headingId, int level = 2)
    {
        return new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject { ["level"] = level, ["headingId"] = headingId },
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        };
    }

    /// <summary>A table as the editor saves one: the first row of header cells, each cell one paragraph.</summary>
    public static JsonObject Table(params string[][] rows) => new()
    {
        ["type"] = "table",
        ["content"] = new JsonArray(rows.Select((row, index) => (JsonNode)new JsonObject
        {
            ["type"] = "tableRow",
            ["content"] = new JsonArray(row.Select(cell => (JsonNode)new JsonObject
            {
                ["type"] = index == 0 ? "tableHeader" : "tableCell",
                ["attrs"] = new JsonObject { ["colspan"] = 1, ["rowspan"] = 1, ["colwidth"] = null },
                ["content"] = new JsonArray(Paragraph(cell)),
            }).ToArray()),
        }).ToArray()),
    };

    /// <summary>A requirement heading (shape 2026-11-04 §1): a heading with a state.</summary>
    public static JsonObject RequirementHeading(string headingId, string title, string state = "planned", string direction = "internal", int level = 3,
        string party = "", string partyContactId = "")
    {
        var heading = Heading(title, headingId, level);
        heading["attrs"]!["state"] = state;
        heading["attrs"]!["direction"] = direction;
        if (party.Length > 0)
        {
            heading["attrs"]!["party"] = party;
        }
        if (partyContactId.Length > 0)
        {
            heading["attrs"]!["partyContactId"] = partyContactId;
        }
        return heading;
    }

    /// <summary>A dependency line: section links to <paramref name="targets"/>, separated by „, “.</summary>
    public static JsonObject Dependencies(params (Guid Page, string HeadingId)[] targets)
    {
        var content = new JsonArray();
        foreach (var (page, heading) in targets)
        {
            if (content.Count > 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = ", " });
            }
            content.Add(new JsonObject
            {
                ["type"] = "pageReference",
                ["attrs"] = new JsonObject { ["id"] = page.ToString("D"), ["title"] = heading, ["url"] = $"#/pages/{page:D}?heading={heading}" },
            });
        }
        return new JsonObject { ["type"] = "paragraph", ["attrs"] = new JsonObject { ["dependencies"] = true }, ["content"] = content };
    }

    public static JsonObject Image(Guid attachment) => new() { ["type"] = "image", ["attrs"] = new JsonObject { ["src"] = $"/api/attachments/{attachment:D}/content" } };

    public static JsonObject Summary(string id) => new() { ["type"] = "overview", ["attrs"] = new JsonObject { ["id"] = id, ["kind"] = "requirements" } };

    /// <summary>The top-level node types and requirement ids of a document, in order: what a test compares after a change.</summary>
    public static string Outline(JsonObject rich)
    {
        return string.Join(" ", rich["document"]!["content"]!.AsArray().Select(Outline));
    }

    private static string Outline(JsonNode? node)
    {
        if (node!["type"]!.GetValue<string>() != "requirement")
        {
            return node["type"]!.GetValue<string>();
        }
        var children = node["content"]!.AsArray().Skip(1).Select(Outline).ToArray();
        var id = node["attrs"]!["id"]!.GetValue<string>();
        return children.Length == 0 ? id : $"{id}[{string.Join(" ", children)}]";
    }
}
