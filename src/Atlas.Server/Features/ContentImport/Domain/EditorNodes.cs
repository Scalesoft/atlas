using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>Builders and small transformations for editor document JSON produced by the import.</summary>
internal static class EditorNodes
{
    public static JsonObject Module(string type, JsonObject data)
    {
        return RichNodes.Node(type, null, new JsonObject { ["data"] = data });
    }

    public static bool IsSafeExternal(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto" && uri.UserInfo.Length == 0;
    }

    public static bool IsInlineImage(string? href)
    {
        return href is not null && href.StartsWith("/api/attachments/", StringComparison.Ordinal) && href.EndsWith("/content", StringComparison.Ordinal);
    }

    /// <summary>
    /// Brings inline content to the form the editor keeps: adjacent text with equal marks is one node,
    /// and whitespace at the edges of a text block carries no meaning once line structure is gone.
    /// </summary>
    public static void Normalize(JsonArray inlines)
    {
        for (var index = inlines.Count - 1; index > 0; index--)
        {
            if (RichDocument.TryMergeText(inlines[index - 1], inlines[index]))
            {
                inlines.RemoveAt(index);
            }
        }
        while (inlines.Count > 0 && (RichDocument.TypeOf(inlines[^1]) == "hardBreak" || RichDocument.TypeOf(inlines[^1]) == "text" && inlines[^1]!["text"]!.GetValue<string>().TrimEnd().Length == 0))
        {
            inlines.RemoveAt(inlines.Count - 1);
        }
        if (inlines.Count > 0 && RichDocument.TypeOf(inlines[^1]) == "text")
        {
            inlines[^1]!["text"] = inlines[^1]!["text"]!.GetValue<string>().TrimEnd();
        }
        while (inlines.Count > 0 && RichDocument.TypeOf(inlines[0]) == "text" && inlines[0]!["text"]!.GetValue<string>().TrimStart().Length == 0)
        {
            inlines.RemoveAt(0);
        }
        if (inlines.Count > 0 && RichDocument.TypeOf(inlines[0]) == "text")
        {
            inlines[0]!["text"] = inlines[0]!["text"]!.GetValue<string>().TrimStart();
        }
    }

    public static string NodeText(JsonArray inlines)
    {
        var builder = new StringBuilder();
        foreach (var node in inlines.OfType<JsonObject>())
        {
            builder.Append(RichDocument.TypeOf(node) switch
            {
                "text" => node["text"]!.GetValue<string>(),
                "hardBreak" => " ",
                "mathInline" => node["attrs"]!["data"]!["expression"]!.GetValue<string>(),
                _ => "",
            });
        }
        return builder.ToString().Trim();
    }

    public static string Collapse(string text)
    {
        return string.Join(" ", text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
    }
}
