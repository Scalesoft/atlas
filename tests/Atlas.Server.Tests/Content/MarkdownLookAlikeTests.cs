using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// A component written the wrong way never leaves its source on the page unnoticed (MCP and REST write through the strict reader):
/// every likely wrong form of every writable component is read as the component, or returns a warning, or is refused as
/// <c>invalid_document</c>. One table, one row per form; the expected outcome is the type of the component read (<c>!</c>: with a
/// warning too), <c>warning</c> (text or code kept, said so) or <c>error</c>.
/// </summary>
public sealed class MarkdownLookAlikeTests
{
    private const string Table = "| a | b |\n| --- | --- |\n| x | 1 |";

    public static TheoryData<string, string> Forms => new()
    {
        // callout: the canonical form is the editor's own; the others are what agents write
        { "> [!WARNING]\n> Jen po schválení.", "callout" },
        { "> [!WARNING] Jen po schválení.", "callout!" },
        { "> [!warning] Jen po schválení.\n> Druhý řádek.", "callout!" },
        { "> [!NOTE:] Jen po schválení.", "callout!" },
        { "> [!FOO] Jen po schválení.", "error" },
        { "[!WARNING] Jen po schválení.", "error" },
        { "!!! warning\n    Jen po schválení.", "warning" },
        { ":::warning\nJen po schválení.\n:::", "warning" },
        // math and mermaid
        { "$$\na + b\n\nc", "warning" },
        { "$$\n$$", "warning" },
        { "```math\n \n```", "warning" },
        { "```latex\nx^2\n```", "warning" },
        { "```math\nx^2\n```", "mathBlock" },
        { "```math zlomek\nx^2\n```", "mathBlock!" },
        { "```mermaid\nA[<b>x</b>]\n```", "warning" },
        { "```Mermaid\nflowchart LR\n  A --> B\n```", "diagram" },
        { "```mermaid Tok schválení\nflowchart LR\n  A --> B\n```", "diagram!" },
        // contacts
        { "```a-contact\n- name: Jana Nováková\n```", "contactCard" },
        { "```contact\nname: Jana Nováková\n```", "warning" },
        { "```A-contact\n- name: Jana Nováková\n```", "warning" },
        { "```a-contact\nJana Nováková\n```", "error" },
        { "[Jana](contact:?name=Jana)", "warning" },
        { "[Jana](a-contact:Jana)", "error" },
        // charts and tables
        { "```a-chart\ntitle: x\n" + Table + "\n```", "error" },
        { "```chart\n---\n" + Table + "\n```", "warning" },
        { "```a-chart\nsource:\n  table: nikde\n  rows: 1-2\n  columns: 1-2\n---\n" + Table + "\n```", "error" },
        { Table + "\n\n{#t}", "warning" },
        { Table, "table" },
        { "| a | b |\n| x | 1 |", "warning" },
        { "| a | b |\n\n| --- | --- |", "warning" },
        { "```a-table\n- rows: []\n```", "error" },
        { "```overview\nkind: contacts\n```", "warning" },
        { "```a-overview\nkind: contacts\n```", "overview" },
        { "```a-overview\nvalue\n```", "error" },
        // task lists
        { "- [ ] Zavolat\n- [x] Napsat", "taskList" },
        { "- [] Zavolat", "warning" },
        { "- [ ]Zavolat", "taskList" },
        { "1. [ ] Zavolat", "warning" },
        // images and attachments
        { "![popis](" + "/api/attachments/" + "00000000-0000-4000-8000-000000000001/content)", "image" },
        { "![popis](https://example.test/a.png)", "warning" },
        { "[popis](" + "/api/attachments/" + "00000000-0000-4000-8000-000000000001/content)", "warning" },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void A_wrong_form_is_read_as_the_component_or_warns_or_is_refused(string markdown, string expected)
    {
        MarkdownToRichResult? result = null;
        ApiException? refused = null;
        try
        {
            result = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true);
        }
        catch (ApiException error)
        {
            refused = error;
        }

        if (expected == "error")
        {
            Assert.NotNull(refused);
            Assert.Equal("invalid_document", refused.Code);
            // Names the line and column and the form to write.
            Assert.Contains("řádek 1, sloupec ", refused.Message, StringComparison.Ordinal);
            return;
        }
        Assert.True(refused is null, refused?.Message);
        var types = result!.Blocks.Select(RichDocument.TypeOf).ToList();
        var component = expected.TrimEnd('!');
        if (expected == "warning")
        {
            Assert.NotEmpty(result.Warnings);
            return;
        }
        Assert.Contains(component, types);
        Assert.Equal(expected.EndsWith('!'), result.Warnings.Count > 0);
        // Nothing is left behind as text: no marker, no fence of a component.
        Assert.DoesNotContain("[!", Texts(result.Blocks), StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_line_callout_warning_names_the_line_the_canonical_form_and_the_syntax_tool()
    {
        var parsed = MarkdownToRich.Parse("Úvod.\n\n> [!WARNING] Kdo soubor dostane, má přístup.", MarkdownToRich.PagePlugins, strict: true);

        var callout = (JsonObject)parsed.Blocks[1]!;
        Assert.Equal("warning", callout["attrs"]!["variant"]!.GetValue<string>());
        Assert.Equal("Kdo soubor dostane, má přístup.", callout["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        var warning = Assert.Single(parsed.Warnings);
        Assert.StartsWith("Řádek 3, sloupec 3:", warning, StringComparison.Ordinal);
        Assert.Contains("„> [!WARNING]“ a text na dalším řádku „> text“ (atlas_get_syntax component=callout)", warning, StringComparison.Ordinal);
        // The editor's own form reads as before and writes back the same.
        var canonical = "> [!WARNING]\n> Kdo soubor dostane, má přístup.";
        var read = MarkdownToRich.Parse(canonical, MarkdownToRich.PagePlugins, strict: true);
        Assert.Empty(read.Warnings);
        Assert.Equal(canonical, EditorMarkdownWriter.Write(new JsonObject { ["type"] = "doc", ["content"] = read.Blocks.DeepClone() }));
        // A quote that really starts with the text "[!NOTE]" is written escaped, so it is no callout and no issue.
        var escaped = MarkdownToRich.Parse("> \\[!NOTE\\] jen citace", MarkdownToRich.PagePlugins, strict: true);
        Assert.Equal(["blockquote"], escaped.Blocks.Select(RichDocument.TypeOf));
        Assert.Empty(escaped.Warnings);
    }

    private static string Texts(JsonArray blocks)
    {
        return string.Concat(blocks.SelectMany(x => x!.ToJsonString().Split('"')).Where(x => x.Contains("[!", StringComparison.Ordinal)));
    }
}
