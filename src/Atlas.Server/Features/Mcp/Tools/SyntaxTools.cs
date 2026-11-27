using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// atlas_get_syntax (5.2.2): the syntax of every component an agent may write into a page, from the one reference next to the reader
/// (<see cref="MarkdownSyntaxReference"/>). It reads no wiki content and needs no database. Not a Wolverine handler: the class name does
/// not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class McpSyntaxTools
{
    public const string ToolName = "atlas_get_syntax";

    [McpServerTool(Name = ToolName, Title = "Syntaxe komponent stránky", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Jak v Markdownu zapsat komponentu stránky (informační box, tabulka, vzorec, diagram, kontakt, graf, přehled…). Bez argumentu přehled, component = jedna.")]
    [Description("Vrátí, jak zapsat do textu stránky každou komponentu: informační box, tabulku, vzorec, diagram Mermaid, kontakt, graf, přehled, odkaz na stránku, obrázek, seznam úkolů. " +
        "Bez argumentu přehled všech komponent a toho, co lze psát, co je jen čtení (značka ⟦c:N⟧) a co nepodporováno; s component přesná syntaxe, všechny volby, minimální a plný příklad, " +
        "časté chyby a co psát místo nich. Zavolejte před prvním zápisem komponenty. Nečte obsah wiki.")]
    public CallToolResult GetSyntax(
        [Description("Id komponenty z přehledu (např. callout, table, math, diagram, contact, chart, overview); vynechte pro přehled všech.")] string? component = null)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return Answer(MarkdownSyntaxReference.Overview(), new JsonObject { ["components"] = new JsonArray(MarkdownSyntaxReference.All.Select(
                x => (JsonNode)new JsonObject { ["id"] = x.Id, ["title"] = x.Title, ["support"] = x.Support.ToString() }).ToArray()) });
        }
        return MarkdownSyntaxReference.Find(component) is { } found
            ? Answer(MarkdownSyntaxReference.Describe(found), new JsonObject { ["component"] = found.Id, ["support"] = found.Support.ToString() })
            : McpErrors.InvalidArgument(MarkdownSyntaxReference.Unknown(component));
    }

    /// <summary>The reference is the server's own text, not wiki content: no trust wrapper, but the text travels in the structured content too.</summary>
    private static CallToolResult Answer(string text, JsonObject structured)
    {
        structured["text"] = text;
        return new CallToolResult { Content = [new TextContentBlock { Text = text }], StructuredContent = JsonSerializer.SerializeToElement(structured) };
    }
}
