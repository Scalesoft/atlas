using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Platform.Content;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// The rich → Markdown projection for MCP and phase 4 (plan 2026-10-05 MCP §9 K1, D8): text blocks become the editor's
/// Markdown, every component a numbered placeholder <c>⟦c:N⟧</c> with a readable label, and sections carry heading ids.
/// </summary>
public sealed partial class RichMarkdownProjectionTests
{
    private static readonly Guid SecretId = Guid.Parse("d847b3af-4a87-4e97-aab8-a74a4cf48c89");
    private static readonly Guid ReadablePage = Guid.Parse("5e940f4d-55c2-4527-addd-10f1d68d742c");
    private static readonly Guid HiddenPage = Guid.Parse("f680cde8-6cc8-4b81-a653-52bc36cd0b75");

    [Trait("Category", "Smoke")]
    [Fact]
    public void Text_blocks_project_and_components_become_numbered_slots()
    {
        var projection = Project(
            Heading(1, "h-projekt", Text("Projekt")),
            Paragraph(Text("Kontakt: "), ContactInline("Jan Novák"), Text(" je vlastník.")),
            List(Paragraph(Text("a")), Paragraph(Text("b"))),
            Table(["A", "B"], ["1", "2"]),
            Code("sh", "echo hi"),
            Module("contactCard", new JsonObject { ["version"] = 2, ["contacts"] = new JsonArray(new JsonObject { ["name"] = "Eva Malá", ["emails"] = new JsonArray("eva@example.com") }) }),
            Heading(2, "h-pristupy", Text("Přístupy")),
            Paragraph(Text("Heslo: "), SecretInline(), Text(".")),
            SecretBlock(),
            Paragraph(Text("Úkol "), new JsonObject { ["type"] = "youtrackIssue", ["attrs"] = new JsonObject { ["key"] = "ABC-12", ["title"] = "Opravit přihlášení", ["url"] = "https://youtrack.example.com/issue/ABC-12", ["state"] = "Open" } }),
            Module("attachment", new JsonObject { ["version"] = 1, ["name"] = "smlouva.pdf", ["url"] = "/api/attachments/0b6b1c38-6b8c-4d9f-9d1e-1b2c3d4e5f60/download" }),
            Module("chart", new JsonObject { ["version"] = 1, ["title"] = "Tržby", ["kind"] = "bar" }),
            // A page title only when the actor may read the page; the title stored in the reference is never used.
            Paragraph(Text("Viz "), PageReference(ReadablePage, "Starý název"), Text(" a "), PageReference(HiddenPage, "Tajný projekt")),
            Paragraph());

        Assert.Equal("""
            # Projekt

            Kontakt: ⟦c:1⟧ je vlastník.

            - a
            - b

            | A | B |
            | --- | --- |
            | 1 | 2 |

            ```sh
            echo hi
            ```

            ⟦c:2⟧

            ## Přístupy

            Heslo: ⟦c:3⟧.

            ⟦c:4⟧

            Úkol ⟦c:5⟧

            ⟦c:6⟧

            ⟦c:7⟧

            Viz ⟦c:8⟧ a ⟦c:9⟧

            """.ReplaceLineEndings("\n"), projection.Markdown);
        Assert.Equal(
        [
            new ComponentSlot(1, "contactInline", "Kontakt: Jan Novák", true),
            new ComponentSlot(2, "contactCard", "Kontakt: Eva Malá", false),
            new ComponentSlot(3, "secretInlineReference", "Odkaz na heslo", true),
            new ComponentSlot(4, "secretReference", "Odkaz na heslo", false),
            new ComponentSlot(5, "youtrackIssue", "Úkol YouTrack: ABC-12", true),
            // The embed tools address the card by its attachment id.
            new ComponentSlot(6, "attachment", "Příloha: smlouva.pdf", false)
            {
                Embeds = [new ComponentEmbed(Guid.Parse("0b6b1c38-6b8c-4d9f-9d1e-1b2c3d4e5f60"), EmbedKind.File, 1)],
            },
            new ComponentSlot(7, "chart", "Graf", false),
            new ComponentSlot(8, "pageReference", "Odkaz na stránku: Provoz", true),
            new ComponentSlot(9, "pageReference", "Odkaz na stránku", true),
        ], projection.Components);
        // The whole text fits one read: a whole-page replacement carries the placeholders back.
        Assert.True(projection.MarkdownEditable);
        // The attachment label names the file, never its URL; an unreadable page leaves no trace of its title.
        var serialized = JsonSerializer.Serialize(projection);
        Assert.DoesNotContain("/api/attachments", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Tajn", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_slots_carry_no_title_or_metadata_and_no_directive()
    {
        var projection = Project(
            Paragraph(Text("VPN "), SecretInline()),
            SecretBlock(),
            // Text that the editor would read as a directive is never offered as Markdown either.
            Paragraph(Text("Zápis :secret{secretId=" + SecretId + "} je jen text.")),
            Code("text", "::secret{secretId=" + SecretId + "}"));

        var serialized = JsonSerializer.Serialize(projection);
        Assert.DoesNotContain(SecretId.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":secret", projection.Markdown, StringComparison.OrdinalIgnoreCase);
        foreach (var field in new[] { "username", "password", "showDetails", "fields" })
        {
            Assert.DoesNotContain(field, serialized, StringComparison.Ordinal);
        }
        Assert.Equal("VPN ⟦c:1⟧\n\n⟦c:2⟧\n\n⟦c:3⟧\n\n⟦c:4⟧\n", projection.Markdown);
        Assert.All(projection.Components.Take(2), slot => Assert.Equal("Odkaz na heslo", slot.Label));
        Assert.All(projection.Components.Skip(2), slot => Assert.DoesNotContain("secret", slot.Label, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Literal_placeholder_characters_are_escaped()
    {
        var projection = Project(
            Paragraph(Text("Viz ⟦c:1⟧, cesta C:\\⟦x⟧ a "), ContactInline("Jan Novák")),
            // Code cannot escape, so code holding a placeholder character is a component, not Markdown.
            Code("text", "⟦c:2⟧"));

        Assert.Equal("Viz \\⟦c:1\\⟧, cesta C:\\\\\\⟦x\\⟧ a ⟦c:1⟧\n\n⟦c:2⟧\n", projection.Markdown);
        // Only the real slots read as placeholders: an odd run of backslashes escapes the bracket.
        Assert.Equal(["⟦c:1⟧", "⟦c:2⟧"], UnescapedPlaceholder().Matches(projection.Markdown).Select(x => x.Value[^5..]));
        Assert.Equal([1, 2], projection.Components.Select(x => x.Index));
        Assert.True(projection.Components[0].Inline);
        Assert.False(projection.Components[1].Inline);

        // The strict parser reads the text back unchanged and the real placeholder as a slot.
        var paragraph = MarkdownToRich.Parse(projection.Markdown).Blocks[0]!;
        var inlines = paragraph["content"]!.AsArray();
        Assert.Equal("Viz ⟦c:1⟧, cesta C:\\⟦x⟧ a ", inlines[0]!["text"]!.GetValue<string>());
        Assert.Equal(MarkdownToRich.SlotNodeType, inlines[1]!["type"]!.GetValue<string>());
        Assert.Equal(1, inlines[1]!["attrs"]!["index"]!.GetValue<int>());
    }

    [Fact]
    public void Sections_carry_heading_ids_and_temporary_ids_for_legacy_headings()
    {
        var projection = Project(7,
            Heading(1, "a1", Text("Úvod")),
            Paragraph(Text("Text")),
            Heading(2, null, Text("Bez ID")),
            Paragraph(Text("A")),
            Heading(2, "b2", Text("Další")),
            Paragraph(Text("B")),
            // A heading id the editor would not accept is not stable either.
            Heading(1, "bad id", Text("Konec")),
            Paragraph(Text("C")));

        Assert.Equal("# Úvod\n\nText\n\n## Bez ID\n\nA\n\n## Další\n\nB\n\n# Konec\n\nC\n", projection.Markdown);
        Assert.Equal(
        [
            new ContentSection("a1", false, 1, "Úvod", 1, 11),
            new ContentSection("v7-h2", true, 2, "Bez ID", 5, 7),
            new ContentSection("b2", false, 2, "Další", 9, 11),
            new ContentSection("v7-h4", true, 1, "Konec", 13, 15),
        ], projection.Sections);
        Assert.True(projection.MarkdownEditable);
    }

    [Fact]
    public void Headings_of_level_five_and_six_are_text_and_read_back_at_their_level()
    {
        // They used to read back as level 4, so the round trip failed and each became a component placeholder.
        var projection = Project(Heading(4, "h4", Text("Čtyři")), Heading(5, "h5", Text("Pět")), Paragraph(Text("A")), Heading(6, "h6", Text("Šest")));

        Assert.Equal("#### Čtyři\n\n##### Pět\n\nA\n\n###### Šest\n", projection.Markdown);
        Assert.Empty(projection.Components);
        Assert.Equal([4, 5, 6], projection.Sections.Select(x => x.Level));
        Assert.Equal([4, 5, 6], MarkdownToRich.Parse(projection.Markdown).Blocks.Where(x => RichDocument.TypeOf(x) == "heading")
            .Select(x => x!["attrs"]!["level"]!.GetValue<int>()));
    }

    [Fact]
    public void A_stored_text_whose_mark_outlives_two_marks_opened_before_it_projects()
    {
        // The editor stores this shape (bold, italic and highlighted text followed by text that is only highlighted); the
        // projection of such a page used to throw, so MCP and AI writing could not read it.
        var projection = Project(Paragraph(Text("a", "bold", "italic", "highlight"), Text("b", "highlight")));

        Assert.Equal("***==a==***==b==\n", projection.Markdown);
        Assert.True(projection.MarkdownEditable);
    }

    [Fact]
    public void A_page_over_the_output_limit_is_not_editable_as_one_text_and_says_so()
    {
        var projection = Project(Paragraph(Text(new string('a', RichMarkdownProjection.OutputLimit + 1))));

        Assert.False(projection.MarkdownEditable);
        Assert.Contains(projection.Warnings, warning => warning.Contains("po sekcích", StringComparison.Ordinal));
    }

    // ---- builders ----

    [GeneratedRegex(@"(?<!\\)(?:\\\\)*⟦c:\d+⟧")]
    private static partial Regex UnescapedPlaceholder();

    private static ContentProjection Project(params JsonObject[] blocks)
    {
        return Project(1, blocks);
    }

    private static ContentProjection Project(int version, params JsonObject[] blocks)
    {
        var envelope = new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(x => (JsonNode)x).ToArray()) },
        };
        return RichMarkdownProjection.Project(RichDocument.Parse(envelope.ToJsonString()),
            new ProjectionOptions(version, page => page == ReadablePage ? "Provoz" : null));
    }

    private static JsonObject PageReference(Guid id, string storedTitle)
    {
        return new JsonObject { ["type"] = "pageReference", ["attrs"] = new JsonObject { ["id"] = id.ToString(), ["title"] = storedTitle, ["url"] = "#/pages/" + id } };
    }

    private static JsonObject Text(string text)
    {
        return new JsonObject { ["type"] = "text", ["text"] = text };
    }

    private static JsonObject Text(string text, params string[] marks)
    {
        return new JsonObject { ["type"] = "text", ["text"] = text, ["marks"] = new JsonArray(marks.Select(x => (JsonNode)new JsonObject { ["type"] = x }).ToArray()) };
    }

    private static JsonObject Paragraph(params JsonObject[] inlines)
    {
        var node = new JsonObject { ["type"] = "paragraph" };
        if (inlines.Length > 0)
        {
            node["content"] = new JsonArray(inlines.Select(x => (JsonNode)x).ToArray());
        }
        return node;
    }

    private static JsonObject Heading(int level, string? id, params JsonObject[] inlines)
    {
        return new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject { ["level"] = level, ["headingId"] = id },
            ["content"] = new JsonArray(inlines.Select(x => (JsonNode)x).ToArray()),
        };
    }

    private static JsonObject List(params JsonObject[] items)
    {
        return new JsonObject
        {
            ["type"] = "bulletList",
            ["attrs"] = new JsonObject { ["tight"] = true },
            ["content"] = new JsonArray(items.Select(x => (JsonNode)new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(x) }).ToArray()),
        };
    }

    private static JsonObject Table(params string[][] rows)
    {
        return new JsonObject
        {
            ["type"] = "table",
            ["content"] = new JsonArray(rows.Select((row, index) => (JsonNode)new JsonObject
            {
                ["type"] = "tableRow",
                ["content"] = new JsonArray(row.Select(cell => (JsonNode)new JsonObject
                {
                    ["type"] = index == 0 ? "tableHeader" : "tableCell",
                    ["attrs"] = new JsonObject { ["colspan"] = 1, ["rowspan"] = 1, ["colwidth"] = null },
                    ["content"] = new JsonArray(Paragraph(Text(cell))),
                }).ToArray()),
            }).ToArray()),
        };
    }

    private static JsonObject Code(string language, string text)
    {
        return new JsonObject { ["type"] = "codeBlock", ["attrs"] = new JsonObject { ["language"] = language }, ["content"] = new JsonArray(Text(text)) };
    }

    private static JsonObject Module(string type, JsonObject data)
    {
        return new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["data"] = data } };
    }

    private static JsonObject ContactInline(string name)
    {
        return Module("contactInline", new JsonObject { ["version"] = 2, ["contacts"] = new JsonArray(new JsonObject { ["name"] = name, ["phones"] = new JsonArray("+420 777 123 456") }) });
    }

    private static JsonObject SecretInline()
    {
        return new JsonObject { ["type"] = "secretInlineReference", ["attrs"] = new JsonObject { ["secretId"] = SecretId.ToString(), ["field"] = "password" } };
    }

    private static JsonObject SecretBlock()
    {
        return new JsonObject
        {
            ["type"] = "secretReference",
            ["attrs"] = new JsonObject { ["secretId"] = SecretId.ToString(), ["fields"] = new JsonArray("username", "password"), ["showDetails"] = true },
        };
    }
}
