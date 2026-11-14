using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// Formulas and Mermaid diagrams in Markdown (spec 2026-10-12 dialect §1.1 bod 2, §6): the server reads <c>$…$</c>, <c>$$…$$</c>,
/// <c>```math</c> and <c>```mermaid</c> as the editor does (markdownInput.ts, markdownImportSyntax.ts) when the page has the plugins,
/// the projection writes them as text, and the merge gives an unchanged one its stored node back.
/// </summary>
public sealed class MarkdownDialectTests
{
    [Fact]
    public void Formulas_and_diagrams_are_read_as_the_editor_reads_them()
    {
        var blocks = Parse("""
            Energie $E = mc^2$ a **$$a + b$$**.

            Cena $5 a $10.

            Cesta $HOME/$USER.

            Znak \$x$ a $ x$.

            $$
            \int_0^1 x\,dx
            $$

            $$x^2$$

            ```math
            a

            b
            ```

            ```mermaid
            flowchart LR
              A --> B
            ```
            """);

        Assert.Equal(["paragraph", "paragraph", "paragraph", "paragraph", "mathBlock", "mathBlock", "mathBlock", "diagram"], blocks.Select(RichDocument.TypeOf));
        var inlines = blocks[0]["content"]!.AsArray();
        Assert.Equal(Math("mathInline", "E = mc^2").ToJsonString(), inlines[1]!.ToJsonString());
        var bold = Math("mathInline", "a + b");
        bold["marks"] = new JsonArray(new JsonObject { ["type"] = "bold" });
        Assert.Equal(bold.ToJsonString(), inlines[3]!.ToJsonString());
        // Pandoc's rules: a closing $ before a digit or letter, a $ after or before a space and an escaped $ are text.
        Assert.Equal(["Cena $5 a $10.", "Cesta $HOME/$USER.", "Znak $x$ a $ x$."],
            blocks.Skip(1).Take(3).Select(x => x["content"]!.AsArray().Single()!["text"]!.GetValue<string>()));
        Assert.Equal(Math("mathBlock", @"\int_0^1 x\,dx").ToJsonString(), blocks[4].ToJsonString());
        Assert.Equal(Math("mathBlock", "x^2").ToJsonString(), blocks[5].ToJsonString());
        Assert.Equal(Math("mathBlock", "a\n\nb").ToJsonString(), blocks[6].ToJsonString());
        Assert.Equal(Diagram("flowchart LR\n  A --> B").ToJsonString(), blocks[7].ToJsonString());
    }

    [Theory]
    // Display math cannot contain a blank line, so an unclosed $$ stays text and the lines after it are Markdown again.
    [InlineData("$$\na + b\n\n# Nadpis", """[{"type":"paragraph","content":[{"type":"text","text":"$$ a + b"}]},{"type":"heading","content":[{"type":"text","text":"Nadpis"}],"attrs":{"level":1}}]""")]
    [InlineData("$$\na\n\nb\n$$", """[{"type":"paragraph","content":[{"type":"text","text":"$$ a"}]},{"type":"paragraph","content":[{"type":"text","text":"b $$"}]}]""")]
    [InlineData("> $$\n> a\n\n$$", """[{"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"$$ a"}]}]},{"type":"paragraph","content":[{"type":"text","text":"$$"}]}]""")]
    // Data the editor's validator refuses stays code: Mermaid with HTML or an init directive, an empty formula.
    [InlineData("```mermaid\nA[<b>x</b>]\n```", """[{"type":"codeBlock","content":[{"type":"text","text":"A[<b>x</b>]"}],"attrs":{"language":"mermaid"}}]""")]
    [InlineData("```mermaid\n%%{init: {}}%%\nA\n```", """[{"type":"codeBlock","content":[{"type":"text","text":"%%{init: {}}%%\nA"}],"attrs":{"language":"mermaid"}}]""")]
    [InlineData("```math\n \n```", """[{"type":"codeBlock","content":[{"type":"text","text":" "}],"attrs":{"language":"math"}}]""")]
    public void Unclosed_or_invalid_dialect_stays_text_or_code(string markdown, string expected)
    {
        var parsed = new JsonArray(Parse(markdown).Select(x => x.DeepClone()).ToArray());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), parsed), parsed.ToJsonString());
    }

    [Fact]
    public void Without_the_plugins_formulas_and_diagrams_stay_text_and_code()
    {
        var blocks = MarkdownToRich.Parse("Vzorec $x$.\n\n$$\ny\n$$\n\n```mermaid\nA --> B\n```\n").Blocks;

        Assert.Equal(["paragraph", "paragraph", "codeBlock"], blocks.Select(RichDocument.TypeOf));
        Assert.Equal("Vzorec $x$.", blocks[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("mermaid", blocks[2]!["attrs"]!["language"]!.GetValue<string>());
        // Only the plugins the page has: diagrams alone leave formulas text.
        Assert.Equal(["paragraph", "paragraph", "diagram"], MarkdownToRich.Parse("Vzorec $x$.\n\n$$\ny\n$$\n\n```mermaid\nA --> B\n```\n", new HashSet<string> { "diagrams" })
            .Blocks.Select(RichDocument.TypeOf));
    }

    [Fact]
    public void Writer_output_reads_back_to_the_same_document_and_writes_the_same_text()
    {
        JsonObject[] blocks =
        [
            Paragraph(Text("Vzorec "), Math("mathInline", @"\frac{a}{b}"), Text(" v textu.")),
            Math("mathBlock", "a = b\nc = d"),
            Math("mathBlock", "a\n\n  b"),
            Diagram("sequenceDiagram\n  A->>B: ```"),
            Table(Paragraph(Text("Norma")), Paragraph(Math("mathInline", "|x| + 1"))),
        ];
        var markdown = EditorMarkdownWriter.Write(Doc(blocks));
        Assert.Contains("$$\na = b\nc = d\n$$", markdown, StringComparison.Ordinal);
        Assert.Contains("```math\na\n\n  b\n```", markdown, StringComparison.Ordinal);
        Assert.Contains("````mermaid\n", markdown, StringComparison.Ordinal);
        Assert.Contains(@"| $\|x\| + 1$ |", markdown, StringComparison.Ordinal);

        var parsed = Parse(markdown);
        Assert.Equal(blocks.Length, parsed.Count);
        for (var index = 0; index < blocks.Length; index++)
        {
            Assert.True(RichDocument.StructurallyEqual(blocks[index], parsed[index]), parsed[index].ToJsonString());
        }
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. parsed])));
    }

    [Fact]
    public void A_captioned_diagram_keeps_its_plain_text_form_and_stays_a_placeholder()
    {
        var captioned = Diagram("flowchart LR\n  A --> B", "Tok schválení");
        var markdown = EditorMarkdownWriter.Write(Doc([captioned]));
        Assert.StartsWith("Tok schválení\nDiagram (Mermaid):\nflowchart LR\n", markdown, StringComparison.Ordinal);
        Assert.Equal(["paragraph"], Parse(markdown).Select(RichDocument.TypeOf));

        var projection = Project(captioned, Diagram("graph TD\n  X --> Y"), Math("mathBlock", " x"));
        Assert.Equal("⟦c:1⟧\n\n```mermaid\ngraph TD\n  X --> Y\n```\n\n⟦c:2⟧\n", projection.Markdown);
        Assert.Equal(["diagram", "mathBlock"], projection.Components.Select(x => x.Type));
    }

    [Fact]
    public void Formulas_project_as_text_and_an_inline_one_Markdown_cannot_carry_stays_a_placeholder()
    {
        var projection = Project(
            Paragraph(Text("Cena "), Math("mathInline", "p = 5"), Text(" a "), Math("mathInline", "a$b"), Text(".")),
            Paragraph(Text("Jen "), Math("mathInline", "x")),
            Math("mathBlock", "y"));

        Assert.Equal("Cena $p = 5$ a ⟦c:1⟧.\n\nJen $x$\n\n$$\ny\n$$\n", projection.Markdown);
        Assert.Equal(["mathInline"], projection.Components.Select(x => x.Type));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Unchanged_formulas_and_diagrams_keep_their_stored_nodes_and_new_ones_declare_their_plugin()
    {
        // Key order of the stored data differs from the parser's: alignment compares as Markdown sees it, the stored node comes back.
        var stored = new JsonObject { ["type"] = "mathBlock", ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["expression"] = "E = mc^2", ["version"] = 1 } } };
        var current = Page(
            [Heading("Fyzika"), Paragraph(Text("Úvod s "), Math("mathInline", "x"), Text(".")), stored, Diagram("graph TD\n  A --> B"), Paragraph()],
            ("math", 1), ("diagrams", 2));
        var projection = RichMarkdownProjection.Project(current, new ProjectionOptions(1));
        Assert.Empty(projection.Components);
        Assert.Equal("# Fyzika\n\nÚvod s $x$.\n\n$$\nE = mc^2\n$$\n\n```mermaid\ngraph TD\n  A --> B\n```\n", projection.Markdown);

        var unchanged = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown, Context(), []);
        Assert.Empty(unchanged.Warnings);
        Assert.True(JsonNode.DeepEquals(current.Document, RichDocument.Parse(unchanged.ContentJson).Document));

        var edited = RichMarkdownMerge.ReplaceAll(current, projection,
            projection.Markdown.Replace("Úvod s", "Nový úvod s", StringComparison.Ordinal) + "\nA nový $y^2$.\n", Context(), []);
        var blocks = RichDocument.Parse(edited.ContentJson).Blocks;
        Assert.True(JsonNode.DeepEquals(stored, blocks[2]));
        Assert.True(JsonNode.DeepEquals(current.Blocks[3], blocks[3]));
        Assert.Equal(Math("mathInline", "x").ToJsonString(), blocks[1]["content"]![1]!.ToJsonString());
        Assert.Equal(Math("mathInline", "y^2").ToJsonString(), blocks[^1]["content"]![1]!.ToJsonString());

        var created = RichMarkdownMerge.CreateDocument("$$\na\n$$\n\n```mermaid\nA --> B\n```\n", Context());
        Assert.Equal(["diagrams", "math"], JsonNode.Parse(created.ContentJson)!["plugins"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal("$$\na\n$$\n\n```mermaid\nA --> B\n```", created.Markdown);
    }

    [Fact]
    public void Lone_dollars_and_unclosed_display_math_stay_linear()
    {
        var dollars = string.Concat(Enumerable.Repeat("$a ", 50_000));
        Assert.Equal("paragraph", RichDocument.TypeOf(Parse(dollars).Single()));
        // Each pass of an unclosed $$ reads its lines again: blocks that stay open are all found at once.
        var open = string.Concat(Enumerable.Repeat("$$\nx\n\n", 5_000));
        Assert.Equal(5_000, Parse(open).Count);
    }

    // ---- a-contact and a-chart (spec §4.3, §4.5; the editor's markdownDialect.test.ts) ----

    private static readonly JsonObject Jana = new()
    {
        ["name"] = "Jana Nováková", ["title"] = "Vedoucí provozu", ["organization"] = "ModelSoft", ["phones"] = new JsonArray("+420 777 123 456"),
        ["emails"] = new JsonArray("jana.novakova@example.test"),
    };

    [Fact]
    public void Contacts_are_written_as_a_fence_and_an_inline_link_and_read_back()
    {
        var petr = new JsonObject
        {
            ["name"] = "Petr", ["emails"] = new JsonArray("p@x.cz"), ["address"] = "Náměstí 1\nBrno", ["kind"] = "internal",
            ["internalUser"] = new JsonObject { ["id"] = "u-7", ["name"] = "Petr S." },
        };
        var inline = Jana.DeepClone().AsObject();
        inline["name"] = "Jana [Nováková]";
        JsonObject[] blocks = [Contacts("contactCard", Jana), Contacts("contactCard", petr), Paragraph(Text("Volejte "), Contacts("contactInline", inline), Text("."))];
        var markdown = string.Join('\n',
            "```a-contact", "- name: Jana Nováková", "  title: Vedoucí provozu", "  organization: ModelSoft", "  phones: [\"+420 777 123 456\"]",
            "  emails: [jana.novakova@example.test]", "```", "", "```a-contact", "- name: Petr", "  emails: [p@x.cz]", "  address: |-", "    Náměstí 1", "    Brno", "  kind: internal",
            "  internalUser:", "    id: u-7", "    name: Petr S.", "```", "",
            @"Volejte [Jana \[Nováková\]](a-contact:?name=Jana%20[Nováková]&title=Vedoucí%20provozu&organization=ModelSoft&phone=+420%20777%20123%20456&email=jana.novakova@example.test).");
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc(blocks)));

        var parsed = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true);
        Assert.Empty(parsed.Warnings);
        Assert.Equal(blocks.Length, parsed.Blocks.Count);
        for (var index = 0; index < blocks.Length; index++)
        {
            Assert.True(RichDocument.StructurallyEqual(blocks[index], parsed.Blocks[index]), parsed.Blocks[index]!.ToJsonString());
        }
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. parsed.Blocks.Select(x => (JsonObject)x!)])));

        // Repeated keys, encoded characters and an internal user; the label is not read.
        var link = Parse("Kontakt: [cokoli](a-contact:?name=Eva%20Malá&phone=+420%20111&phone=222&email=eva@x.cz&address=A%26B%0AC&internalUser.id=u1)")[0];
        Assert.Equal(Contacts("contactInline", new JsonObject
        {
            ["name"] = "Eva Malá", ["internalUser"] = new JsonObject { ["id"] = "u1" }, ["address"] = "A&B\nC", ["phones"] = new JsonArray("+420 111", "222"),
            ["emails"] = new JsonArray("eva@x.cz"),
        }).ToJsonString(), link["content"]![1]!.ToJsonString());
    }

    [Fact]
    public void A_fence_listing_several_contacts_reads_as_consecutive_single_contact_cards()
    {
        // One contact per block (Atlas 3.2): a fence of several people (MCP's Markdown, an older card's projection) is a card each, in
        // order, each with its snapshot as written.
        const string id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
        var markdown = $"Úvod\n\n```a-contact\n- name: Jana\n  objectId: {id}\n  role: PM projektu\n- name: Petr\n  kind: internal\n  username: petr\n- name: Eva\n```";
        var blocks = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true).Blocks.Select(x => (JsonObject)x!).ToList();
        Assert.Equal(["paragraph", "contactCard", "contactCard", "contactCard"], blocks.Select(x => (string)x["type"]!));
        Assert.Equal(["Jana", "Petr", "Eva"], blocks.Skip(1).Select(x => (string)Assert.Single(x["attrs"]!["data"]!["contacts"]!.AsArray())!["name"]!));
        var jana = blocks[1]["attrs"]!["data"]!["contacts"]![0]!;
        Assert.Equal((id, "PM projektu"), ((string?)jana["objectId"], (string?)jana["role"]));
        var petr = blocks[2]["attrs"]!["data"]!["contacts"]![0]!;
        Assert.Equal(("internal", "petr"), ((string?)petr["kind"], (string?)petr["username"]));
        Assert.All(blocks.Skip(1), x => Assert.Equal(1, (int)x["attrs"]!["data"]!["version"]!));
        var written = EditorMarkdownWriter.Write(Doc([.. blocks]));
        Assert.Equal(3, written.Split("```a-contact").Length - 1);
        Assert.Equal(written, EditorMarkdownWriter.Write(Doc([.. MarkdownToRich.Parse(written, MarkdownToRich.PagePlugins).Blocks.Select(x => (JsonObject)x!)])));
    }

    [Fact]
    public void A_contact_keeps_the_directory_object_id_in_both_forms_and_drops_an_invalid_one()
    {
        const string id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
        var markdown = $"```a-contact\n- name: Jana\n  directoryId: d1\n  objectId: {id}\n```\n\nVolejte [Petr](a-contact:?name=Petr&objectId={id}).";
        var blocks = Parse(markdown);
        var card = blocks[0]["attrs"]!["data"]!["contacts"]![0]!;
        Assert.Equal(id, (string?)card["objectId"]);
        Assert.Equal(id, (string?)blocks[1]["content"]![1]!["attrs"]!["data"]!["contacts"]![0]!["objectId"]);
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. blocks])));
        // The editor drops what is no GUID and lowers the case; the server's normal form is the same.
        var data = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(
            new JsonObject { ["name"] = "A", ["objectId"] = id.ToUpperInvariant() }, new JsonObject { ["name"] = "B", ["objectId"] = "u1" }) };
        var normalized = MarkdownDialect.NormalizeContacts(data)["contacts"]!.AsArray();
        Assert.Equal(id, (string?)normalized[0]!["objectId"]);
        Assert.Null(normalized[1]!["objectId"]);
    }

    [Fact]
    public void An_unresolved_contact_stays_unresolved_in_both_forms_until_a_person_is_picked()
    {
        var markdown = "```a-contact\n- name: Jana Nováková\n  unresolved: true\n```\n\nVolala [Jana Nováková](a-contact:?name=Jana%20Nováková&unresolved=true).";
        var blocks = Parse(markdown);
        Assert.Equal(true, (bool?)blocks[0]["attrs"]!["data"]!["contacts"]![0]!["unresolved"]);
        Assert.Equal(true, (bool?)blocks[1]["content"]![1]!["attrs"]!["data"]!["contacts"]![0]!["unresolved"]);
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. blocks])));
        // As the editor: a picked person (objectId) is resolved, anything but true is dropped.
        const string id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
        var data = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = "A", ["unresolved"] = true, ["objectId"] = id },
            new JsonObject { ["name"] = "B", ["unresolved"] = "yes" }, new JsonObject { ["name"] = "C", ["unresolved"] = "true" }) };
        var normalized = MarkdownDialect.NormalizeContacts(data)["contacts"]!.AsArray();
        Assert.Equal([null, null, true], normalized.Select(x => (bool?)x!["unresolved"]));
        Assert.True(Atlas.Server.Platform.Content.ContactSnapshots.IsUnresolved(normalized[2]!.AsObject()));
    }

    [Fact]
    public void A_contact_keeps_the_page_local_role_in_both_forms_right_after_the_name()
    {
        var markdown = "```a-contact\n- name: Jana\n  role: PM projektu\n  title: Vedoucí\n```\n\nVolejte [Petr](a-contact:?name=Petr&role=Garant).";
        var blocks = Parse(markdown);
        Assert.Equal("PM projektu", (string?)blocks[0]["attrs"]!["data"]!["contacts"]![0]!["role"]);
        Assert.Equal("Garant", (string?)blocks[1]["content"]![1]!["attrs"]!["data"]!["contacts"]![0]!["role"]);
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. blocks])));
        var data = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = "A", ["role"] = "  PM  " }) };
        Assert.Equal("PM", (string?)MarkdownDialect.NormalizeContacts(data)["contacts"]![0]!["role"]);
    }

    [Fact]
    public void Plain_scalars_of_text_fields_read_as_written_and_the_writer_quotes_them()
    {
        var markdown = "```a-contact\n- name: 2026\n  title: 1.50\n  organization: true\n  phones: [+420777123456, 0042 1]\n  emails:\n    - 123456@x.cz\n  internalUser:\n    id: 007\n```";
        var card = Parse(markdown).Single();
        Assert.Equal(Contacts("contactCard", new JsonObject
        {
            ["name"] = "2026", ["internalUser"] = new JsonObject { ["id"] = "007" }, ["organization"] = "true", ["title"] = "1.50",
            ["phones"] = new JsonArray("+420777123456", "0042 1"), ["emails"] = new JsonArray("123456@x.cz"),
        }).ToJsonString(), card.ToJsonString());
        var written = EditorMarkdownWriter.Write(Doc([card]));
        Assert.Equal("```a-contact\n- name: \"2026\"\n  title: \"1.50\"\n  organization: \"true\"\n  phones: [\"+420777123456\", 0042 1]\n  emails: [123456@x.cz]\n  internalUser:\n    id: \"007\"\n```", written);
        Assert.Equal(written, EditorMarkdownWriter.Write(Doc([.. Parse(written)])));
        // Fields that are not strings keep the core schema: kind 1 is a number, not a display kind.
        Assert.Equal("invalid_document", Assert.Throws<ApiException>(() => MarkdownToRich.Parse("```a-contact\n- name: Jana\n  kind: 1\n```", MarkdownToRich.PagePlugins, strict: true)).Code);
    }

    [Fact]
    public void Invalid_contacts_are_errors_with_their_position_in_strict_mode_and_code_or_text_in_lenient_mode()
    {
        var markdown = string.Join('\n',
            "Úvod", "", "```a-contact", "- name: Jana", "  fax: 123", "```", "", "```a-contact", "- name: Jana", "  emails: [not-an-email]", "```", "",
            "> Text [Jana](a-contact:?name=Jana&fax=1) a [x](a-contact:?title=Bez%20jména).", "", "```a-contact", "- name: Jana", "  title: [a", "```");
        var strict = Assert.Throws<ApiException>(() => MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true));
        Assert.Equal((400, "invalid_document"), (strict.Status, strict.Code));
        var errors = (List<Dictionary<string, object?>>)strict.Extensions!["errors"]!;
        Assert.Equal([(3, 1), (8, 1), (13, 8), (13, 45), (15, 1)], errors.Select(x => ((int)x["line"]!, (int)x["column"]!)));
        Assert.Equal((3, 1), ((int)strict.Extensions["line"]!, (int)strict.Extensions["column"]!));
        Assert.Equal("Neplatná komponenta a-contact: Neznámý klíč fax.", errors[0]["message"]);
        Assert.StartsWith("Neplatná komponenta a-contact: ", (string)errors[1]["message"]!, StringComparison.Ordinal);
        Assert.Equal("Neplatná komponenta a-contact: Řádek 2 těla: nepodporovaný zápis (a-YAML dovoluje mapy, seznamy, krátké seznamy [a, b] a bloky |; kotvy, aliasy, tagy, flow mapy ani víc dokumentů ne).",
            errors[4]["message"]);
        Assert.Contains("řádek 3, sloupec 1: Neplatná komponenta a-contact: Neznámý klíč fax.", strict.Message, StringComparison.Ordinal);

        var lenient = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins);
        Assert.Equal(5, lenient.Warnings.Count);
        var blocks = lenient.Blocks.Select(x => (JsonObject)x!).ToList();
        Assert.Equal([("paragraph", null), ("codeBlock", "a-contact"), ("codeBlock", "a-contact"), ("blockquote", null), ("codeBlock", "a-contact")],
            blocks.Select(x => (RichDocument.TypeOf(x), x["attrs"]?["language"]?.GetValue<string>())));
        Assert.Equal("Text [Jana](a-contact:?name=Jana&fax=1) a [x](a-contact:?title=Bez%20jména).", blocks[3]["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        // A fence read leniently is a code block and stays one: written in the reserved a-code form, it reads back as the same code.
        var written = EditorMarkdownWriter.Write(Doc([.. blocks]));
        Assert.Contains("```a-code a-contact\n- name: Jana\n  fax: 123\n```", written, StringComparison.Ordinal);
        Assert.Equal(written, EditorMarkdownWriter.Write(Doc([.. MarkdownToRich.Parse(written, MarkdownToRich.PagePlugins, strict: true).Blocks.Select(x => (JsonObject)x!)])));
    }

    [Fact]
    public void A_fence_of_several_people_splits_as_the_editor_does_an_indented_list_too_each_item_validated_on_its_own()
    {
        // The same inputs as markdownDialect.test.ts.
        List<string> People(string markdown) => [.. MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins).Blocks.Select(x => (JsonObject)x!)
            .Select(x => RichDocument.TypeOf(x) == "contactCard" ? string.Join(",", x["attrs"]!["data"]!["contacts"]!.AsArray().Select(c => (string)c!["name"]!)) : RichDocument.TypeOf(x))];
        Assert.Equal(["Jana", "Petr"], People("```a-contact\n  - name: Jana\n  - name: Petr\n```"));
        const string invalid = "```a-contact\n- name: Jana\n- name: Petr\n  fax: 1\n- name: Eva\n```";
        Assert.Equal(["Jana", "codeBlock", "Eva"], People(invalid));
        var strict = Assert.Throws<ApiException>(() => MarkdownToRich.Parse(invalid, MarkdownToRich.PagePlugins, strict: true));
        var error = Assert.Single((List<Dictionary<string, object?>>)strict.Extensions!["errors"]!);
        // Each part's error points at its - line, the first's too (after a comment).
        Assert.Equal((3, "Neplatná komponenta a-contact: Neznámý klíč fax."), ((int)error["line"]!, (string?)error["message"]));
        var first = Assert.Throws<ApiException>(() => MarkdownToRich.Parse("```a-contact\n# lidé\n- name: Jana\n  fax: 1\n- name: Petr\n```", MarkdownToRich.PagePlugins, strict: true));
        Assert.Equal(3, (int)Assert.Single((List<Dictionary<string, object?>>)first.Extensions!["errors"]!)["line"]!);
        Assert.Equal(101, People("```a-contact\n" + string.Join('\n', Enumerable.Range(0, 101).Select(i => $"- name: K{i}")) + "\n```").Count);
        Assert.Equal(["codeBlock"], People("```a-contact\nlidé:\n- name: Jana\n- name: Petr\n```"));
    }

    [Fact]
    public void Charts_without_a_source_are_written_and_read_with_escaped_cells()
    {
        var plain = Chart("", "bar", [["Období", "Tržby"], ["Leden", "12,5"], ["Únor", "18"]]);
        const string markdown = "```a-chart\n---\n| Období | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n```";
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([plain])));
        Assert.True(RichDocument.StructurallyEqual(plain, Parse(markdown).Single()));
        Assert.Equal("```a-chart\ntitle: Tržby 2026\nkind: line\n---\n| Období | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n```",
            EditorMarkdownWriter.Write(Doc([Chart("Tržby 2026", "line", [["Období", "Tržby"], ["Leden", "12,5"], ["Únor", "18"]])])));

        var escaped = Chart("", "bar", [["Kategorie", "A|B"], [@"C:\data\n", "1"], ["dva\nřádky", "2"]]);
        var written = EditorMarkdownWriter.Write(Doc([escaped]));
        Assert.Contains("| Kategorie | A\\|B |\n| --- | --- |\n| C:\\\\data\\\\n | 1 |\n| dva\\nřádky | 2 |", written, StringComparison.Ordinal);
        Assert.True(RichDocument.StructurallyEqual(escaped, Parse(written).Single()));
        // A cell with a carriage return has no escape: the chart keeps its readable text form.
        Assert.DoesNotContain("a-chart", EditorMarkdownWriter.Write(Doc([Chart("", "bar", [["a", "b"], ["x\ry", "1"]])])), StringComparison.Ordinal);
    }

    [Fact]
    public void A_linked_chart_names_its_table_and_reads_back_with_new_cell_ids()
    {
        var markdown = string.Join('\n',
            "| Pobočka | Období | Tržby |", "| --- | --- | --- |", "| Brno | Leden | 12,5 |", "| Praha | Únor | 18 |", "", "{#tabulka-trzby}", "",
            "```a-chart", "title: Tržby 2026", "kind: line", "source:", "  table: tabulka-trzby", "  rows: 1-3", "  columns: 2-3", "---",
            "| Období | Tržby |", "| --- | --- |", "| Leden | 12,5 |", "| Únor | 18 |", "```");
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([SalesTable(), LinkedChart()])));

        var next = 0;
        var parsed = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true, () => "c" + ++next);
        Assert.Empty(parsed.Warnings);
        var table = (JsonObject)parsed.Blocks[0]!;
        Assert.Equal("tabulka-trzby", table["attrs"]!["chartRef"]!.GetValue<string>());
        var ids = table["content"]!.AsArray().Select(row => row!["content"]!.AsArray().Select(cell => cell!["attrs"]!["chartRef"]!.GetValue<string>()).ToList()).ToList();
        Assert.Equal([["c1", "c2", "c3"], ["c4", "c5", "c6"], ["c7", "c8", "c9"]], ids);
        var data = parsed.Blocks[1]!["attrs"]!["data"]!;
        Assert.Equal("""{"tableId":"tabulka-trzby","cells":[["c2","c3"],["c5","c6"],["c8","c9"]]}""", data["source"]!.ToJsonString());
        Assert.Equal("linked", data["sourceStatus"]!.GetValue<string>());
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. parsed.Blocks.Select(x => (JsonObject)x!)])));

        // A number-like title and table id read as text; the writer quotes them.
        var numeric = "| a | b |\n| --- | --- |\n| x | 1 |\n\n{#2026}\n\n```a-chart\ntitle: 2026\nsource:\n  table: 2026\n  rows: 1-2\n  columns: 1-2\n---\n| a | b |\n| --- | --- |\n| x | 1 |\n```";
        var read = Parse(numeric);
        Assert.Equal("2026", read[0]["attrs"]!["chartRef"]!.GetValue<string>());
        Assert.Equal(numeric.Replace("title: 2026", "title: \"2026\"", StringComparison.Ordinal).Replace("table: 2026", "table: \"2026\"", StringComparison.Ordinal),
            EditorMarkdownWriter.Write(Doc([.. read])));
        Assert.Equal("Neplatná komponenta a-chart: Neplatný odkaz na tabulku.", StrictErrors(numeric.Replace("rows: 1-2", "rows: 2", StringComparison.Ordinal)).Single());
    }

    [Fact]
    public void The_linked_table_wins_over_the_grid_and_an_id_paragraph_is_text_unless_a_chart_names_it()
    {
        var markdown = "| Měsíc | Kusy |\n| --- | --- |\n| Leden | 7 |\n\n{#t1}\n\n```a-chart\nsource:\n  table: t1\n  rows: 1-2\n  columns: 1-2\n---\n| Měsíc | Kusy |\n| --- | --- |\n| Leden | 99 |\n```";
        var parsed = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true);
        Assert.Equal(["Řádek 7, sloupec 1: Mřížka grafu se liší od tabulky {#t1}; platí hodnoty tabulky."], parsed.Warnings);
        Assert.Equal("""[["Měsíc","Kusy"],["Leden","7"]]""", parsed.Blocks[1]!["attrs"]!["data"]!["grid"]!.ToJsonString(RichDocument.JsonWrite));
        // Without a chart naming it, {#id} stays text; text that would name a linked table is escaped.
        Assert.Equal("{#t1}", Parse("| a | b |\n| --- | --- |\n| x | 1 |\n\n{#t1}")[1]["content"]![0]!["text"]!.GetValue<string>());
        Assert.EndsWith("```\n\n\\{#tabulka-trzby}", EditorMarkdownWriter.Write(Doc([SalesTable(), LinkedChart(), Paragraph(Text("{#tabulka-trzby}"))])), StringComparison.Ordinal);
        // A chart whose table the dialect cannot name keeps its readable text form.
        var missing = LinkedChart();
        missing["attrs"]!["data"]!["source"]!["tableId"] = "nikde";
        Assert.DoesNotContain("a-chart", EditorMarkdownWriter.Write(Doc([SalesTable(), missing])), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```a-chart\nsource:\n  table: nikde\n  rows: 1-2\n  columns: 1-2\n---\n| a | b |\n| --- | --- |\n| x | 1 |\n```", "Tabulka {#nikde} pro graf neexistuje.")]
    [InlineData("| a | b |\n| --- | --- |\n| x | 1 |\n\n{#t}\n\n```a-chart\nsource:\n  table: t\n  rows: 1-5\n  columns: 1-2\n---\n| a | b |\n| --- | --- |\n| x | 1 |\n```", "Neplatný odkaz na tabulku.")]
    [InlineData("```a-chart\n---\n| a | b |\n| x | 1 |\n```", "Graf potřebuje za řádkem --- mřížku jako tabulku Markdownu (záhlaví, oddělovač, řádky).")]
    [InlineData("```a-chart\ncolour: red\n---\n| a | b |\n| --- | --- |\n| x | 1 |\n```", "Neznámý klíč colour.")]
    [InlineData("```a-chart\nkind: pie\n---\n| a | b |\n| --- | --- |\n| x | -1 |\n```", null)]
    public void An_invalid_chart_is_an_error_in_strict_mode_and_a_code_block_in_lenient_mode(string markdown, string? reason)
    {
        var error = StrictErrors(markdown).Single();
        Assert.StartsWith("Neplatná komponenta a-chart: " + reason, error, StringComparison.Ordinal);
        var last = Parse(markdown)[^1];
        Assert.Equal(("codeBlock", "a-chart"), (RichDocument.TypeOf(last), last["attrs"]!["language"]!.GetValue<string>()));
    }

    [Fact]
    public void The_a_namespace_is_reserved_for_components()
    {
        var code = RichNodes.Node("codeBlock", RichNodes.Text("title: x\n---"), new JsonObject { ["language"] = "a-chart" });
        Assert.Equal("```a-code a-chart\ntitle: x\n---\n```", EditorMarkdownWriter.Write(Doc([code])));
        Assert.True(RichDocument.StructurallyEqual(code, Parse("```a-code a-chart\ntitle: x\n---\n```").Single()));
        Assert.Equal("mermaid", Parse("```a-code mermaid\nA --> B\n```").Single()["attrs"]!["language"]!.GetValue<string>());

        // Unknown names, and a-contact without the contacts plugin, are errors; leniently they stay code and text.
        const string markdown = "```a-contact\n- name: Jana\n```\n\n```a-page\nid: 1\n```\n\nViz [stránku](a-page:42).";
        var plugins = new HashSet<string> { "charts" };
        var strict = Assert.Throws<ApiException>(() => MarkdownToRich.Parse(markdown, plugins, strict: true));
        Assert.Equal([(1, 1, "Neznámá komponenta a-contact."), (5, 1, "Neznámá komponenta a-page."), (9, 5, "Neznámá komponenta a-page.")],
            ((List<Dictionary<string, object?>>)strict.Extensions!["errors"]!).Select(x => ((int)x["line"]!, (int)x["column"]!, (string)x["message"]!)));
        var lenient = MarkdownToRich.Parse(markdown, plugins).Blocks;
        Assert.Equal("```a-code a-contact\n- name: Jana\n```\n\n```a-code a-page\nid: 1\n```\n\nViz \\[stránku\\](a-page:42).",
            EditorMarkdownWriter.Write(Doc([.. lenient.Select(x => (JsonObject)x!)])));
        // Without any plugin set (plain Markdown readers) nothing of the dialect is read.
        Assert.Equal("a-code", MarkdownToRich.Parse("```a-code a-x\n```").Blocks.Single()!["attrs"]!["language"]!.GetValue<string>());
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Unchanged_contacts_and_charts_project_as_text_and_keep_their_stored_nodes()
    {
        var card = Contacts("contactCard", Jana);
        var chart = Chart("Tržby", "bar", [["Měsíc", "Tržby"], ["Leden", "12"]]);
        var current = Page(
            [Heading("Kontakty"), Paragraph(Text("Volejte "), Contacts("contactInline", Jana), Text(".")), card, chart, SalesTable(), LinkedChart(), Paragraph()],
            ("charts", 1), ("contacts", 2));
        var projection = RichMarkdownProjection.Project(current, new ProjectionOptions(1));
        // A linked chart and its table name each other: they stay placeholders (the table carries its chartRef).
        Assert.Equal(["table", "chart"], projection.Components.Select(x => x.Type));
        Assert.Contains("```a-contact\n- name: Jana Nováková", projection.Markdown, StringComparison.Ordinal);
        Assert.Contains("Volejte [Jana Nováková](a-contact:?name=Jana%20Nováková&", projection.Markdown, StringComparison.Ordinal);
        Assert.Contains("```a-chart\ntitle: Tržby\n---\n", projection.Markdown, StringComparison.Ordinal);

        var unchanged = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown, Context(), []);
        Assert.Empty(unchanged.Warnings);
        Assert.True(JsonNode.DeepEquals(current.Document, RichDocument.Parse(unchanged.ContentJson).Document));

        // Text edited around them: the contact nodes and the chart come back as stored; a new chart and contact are the editor's nodes.
        var edited = RichMarkdownMerge.ReplaceAll(current, projection,
            projection.Markdown.Replace("Volejte ", "Volejte prosím ", StringComparison.Ordinal)
            + "\n```a-contact\n- name: Nový\n```\n\n```a-chart\nkind: pie\n---\n| A | B |\n| --- | --- |\n| x | 1 |\n```\n", Context(), []);
        var blocks = RichDocument.Parse(edited.ContentJson).Blocks;
        Assert.True(JsonNode.DeepEquals(current.Blocks[1]["content"]![1], blocks[1]["content"]![1]));
        Assert.True(JsonNode.DeepEquals(card, blocks[2]));
        Assert.True(JsonNode.DeepEquals(chart, blocks[3]));
        Assert.Equal(["contactCard", "chart", "paragraph"], blocks.Skip(7).Select(RichDocument.TypeOf));

        // An invalid component in a write is refused with its position and nothing is merged.
        var refused = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown + "\n```a-chart\n---\n| a |\n```\n", Context(), []));
        Assert.Equal((400, "invalid_document"), (refused.Status, refused.Code));
        Assert.Equal(projection.Markdown.Count(x => x == '\n') + 2, (int)refused.Extensions!["line"]!);
    }

    /// <summary>The overview (src/Atlas.Web/src/overview/overviewNode.ts, overview.test.ts): a kind without a line is the fence ```a-overview, read back
    /// as the same overview (its id is the editor's, kept by the merge); the requirements kind keeps its readable line and stays a placeholder.</summary>
    [Fact]
    public void Overviews_are_written_as_the_a_overview_fence_and_read_back_requirements_keep_their_line()
    {
        JsonObject Overview(string kind, string? id = null) => RichNodes.Node("overview", null, new JsonObject { ["id"] = id, ["kind"] = kind });
        var markdown = "```a-overview\nkind: contacts\n```\n\n```a-overview\nkind: issues\n```\n\n**Stav nasazení:** 0 % hotovo (0 z 0)";
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([Overview("contacts", "6f1c1d3e-8a4b-4c55-9d1e-000000000001"), Overview("issues"), Overview("requirements")])));

        var parsed = MarkdownToRich.Parse("```a-overview\nkind: contacts\n```\n\n```a-overview\n# úkoly\nkind: \"issues\"\n```\n\n```a-overview\nkind: requirements\n```",
            MarkdownToRich.PagePlugins, strict: true);
        Assert.Equal(["contacts", "issues", "requirements"], parsed.Blocks.Select(x => x!["attrs"]!["kind"]!.GetValue<string>()));
        Assert.All(parsed.Blocks, x => Assert.Equal("overview", RichDocument.TypeOf(x)));

        var projection = Project(Overview("contacts", "6f1c1d3e-8a4b-4c55-9d1e-000000000001"), Overview("requirements", "6f1c1d3e-8a4b-4c55-9d1e-000000000002"));
        Assert.StartsWith("```a-overview\nkind: contacts\n```", projection.Markdown, StringComparison.Ordinal);
        Assert.Equal(["overview"], projection.Components.Select(x => x.Type));
        Assert.Equal("**Stav nasazení:** 0 % hotovo (0 z 0)", projection.Components[0].Text);

        Assert.Contains("Neplatná komponenta a-overview", StrictErrors("```a-overview\nkind: Kontakty\nlevel: s\n```").Single(), StringComparison.Ordinal);
        Assert.Equal("codeBlock", RichDocument.TypeOf(Parse("```a-overview\nkind: Kontakty\n```")[0]));
        Assert.Equal("codeBlock", RichDocument.TypeOf(MarkdownToRich.Parse("```a-overview\nkind: contacts\n```", new HashSet<string> { "contacts" }).Blocks[0]));
    }

    /// <summary>
    /// A requirement's note (plan 3.5 B, web requirementCommands.ts historyNote): a list item whose first line starts with its day,
    /// formatted, with a second paragraph and a list nested under it. „29. 10. 2026 —“ reads like an ordered list, so it is written escaped;
    /// MCP reads and writes the notes as this Markdown (notesMarkdown), and they come back as the same records, never as nested ordered lists.
    /// </summary>
    [Fact]
    public void A_requirement_note_keeps_its_day_and_its_format_through_Markdown()
    {
        static JsonObject Item(params JsonObject[] content) => RichNodes.Node("listItem", new JsonArray(content.Select(x => (JsonNode)x).ToArray()));
        static JsonObject Bullets(bool tight, params JsonObject[] items) =>
            RichNodes.Node("bulletList", new JsonArray(items.Select(x => (JsonNode)x).ToArray()), new JsonObject { ["tight"] = tight });
        static JsonObject Marked(string text, string mark) => new() { ["type"] = "text", ["text"] = text, ["marks"] = new JsonArray(new JsonObject { ["type"] = mark }) };
        // The records are a timed list (rich revision 5): a bullet list whose first item starts with a stamp reads back as one.
        static JsonObject Timed(params JsonObject[] items)
        {
            var list = Bullets(false, items);
            list["type"] = "timedList";
            foreach (var item in items)
            {
                item["type"] = "timedItem";
            }
            return list;
        }
        JsonObject[] blocks =
        [
            Paragraph(Text("Přístup k bráně.")),
            Timed(
                Item(Paragraph(Text("29. 10. 2026 — "), Marked("Telefon", "bold"), Text(" s "), Marked("IT", "italic")), Paragraph(Text("druhý řádek")),
                    Bullets(true, Item(Paragraph(Text("certifikát zítra"))), Item(Paragraph(Text("VPN v pátek"))))),
                Item(Paragraph(Text("12. 10. 2026 14:05 — Vyzváno: IT KBN")))),
        ];
        var markdown = EditorMarkdownWriter.Write(Doc(blocks));
        Assert.Contains("- 29\\. 10. 2026 — **Telefon** s *IT*\n\n  druhý řádek\n\n  - certifikát zítra\n  - VPN v pátek", markdown, StringComparison.Ordinal);
        Assert.Contains("- 12\\. 10. 2026 14:05 — Vyzváno: IT KBN", markdown, StringComparison.Ordinal);

        var parsed = Parse(markdown);
        Assert.Equal(blocks.Length, parsed.Count);
        for (var index = 0; index < blocks.Length; index++)
        {
            Assert.True(RichDocument.StructurallyEqual(blocks[index], parsed[index]), parsed[index].ToJsonString());
        }
        Assert.DoesNotContain("orderedList", string.Concat(parsed.Select(x => x.ToJsonString())), StringComparison.Ordinal);
        Assert.Equal(markdown, EditorMarkdownWriter.Write(Doc([.. parsed])));
    }

    /// <summary>The MCP help's history line, written unescaped („- 30. 10. 2026 23:40 — …“, Atlas 3.5.1): one list item of text starting
    /// with the day, as the editor writes it, also as a plain line; a date in fenced code stays as it is.</summary>
    [Fact]
    public void An_unescaped_history_line_is_one_dated_line_not_nested_ordered_lists()
    {
        var parsed = Parse("- 30. 10. 2026 23:40 — **Urgováno**: IT KBN\n- 1. 11. 2026 — telefonát\n\n1. 2. 2026 odstavec\n\n```\n1. 2. 2026 log\n```");

        Assert.DoesNotContain("orderedList", string.Concat(parsed.Select(x => x.ToJsonString())), StringComparison.Ordinal);
        Assert.Equal("- 30\\. 10. 2026 23:40 — **Urgováno**: IT KBN\n- 1\\. 11. 2026 — telefonát\n\n1\\. 2. 2026 odstavec\n\n```\n1. 2. 2026 log\n```",
            EditorMarkdownWriter.Write(Doc([.. parsed])));
    }

    /// <summary>A timed list (rich revision 5) is a bullet list whose first item starts with a stamp, a fresh one without text too; the
    /// Markdown is a bullet list's either way.</summary>
    [Fact]
    public void A_bullet_list_starting_with_a_stamp_is_a_timed_list()
    {
        Assert.Equal(["timedList", "bulletList", "orderedList"], new[] { "- 3\\. 11. 2026 17:19 —\n- b", "- a\n- 3\\. 11. 2026 — b", "1. 2026\n2. b" }
            .Select(markdown => RichDocument.TypeOf(Parse(markdown)[0])));
        var parsed = Parse("- 3. 11. 2026 17:19 — a\n- b");
        Assert.Equal(["timedItem", "timedItem"], parsed[0]["content"]!.AsArray().Select(RichDocument.TypeOf));
        Assert.Equal("- 3\\. 11. 2026 17:19 — a\n- b", EditorMarkdownWriter.Write(Doc([.. parsed])));
    }

    /// <summary>
    /// A table with a list in a cell was stored as loose paragraphs: the writer flattened every table GFM cannot carry.
    /// It is the dialect's ```a-table (§4.12) now, and reads back as the same table, a merged cell and a table inside a list item too.
    /// </summary>
    [Fact]
    public void A_table_with_blocks_or_merged_cells_is_written_as_a_table_and_reads_back()
    {
        static JsonObject Cell(string type, JsonObject? attributes, params JsonObject[] content) =>
            RichNodes.Node(type, new JsonArray(content.Select(x => (JsonNode)x).ToArray()), attributes);
        static JsonObject Row(params JsonObject[] cells) => RichNodes.Node("tableRow", new JsonArray(cells.Select(x => (JsonNode)x).ToArray()));
        var list = RichNodes.Node("bulletList", new JsonArray(
            RichNodes.Node("listItem", new JsonArray(Paragraph(Text("a")))), RichNodes.Node("listItem", new JsonArray(Paragraph(Text("b"))))), new JsonObject { ["tight"] = true });
        var table = RichNodes.Node("table", new JsonArray(
            Row(Cell("tableHeader", null, Paragraph(Text("Fáze"))), Cell("tableHeader", new JsonObject { ["textAlign"] = "center", ["align"] = "center" }, Paragraph(Text("Popis")))),
            Row(Cell("tableCell", null, Paragraph(Text("Návrh"))), Cell("tableCell", null, Paragraph(Text("Body:")), list))));
        var merged = RichNodes.Node("table", new JsonArray(
            Row(Cell("tableHeader", new JsonObject { ["colspan"] = 2 }, Paragraph(Text("Spojené")))),
            Row(Cell("tableCell", null, Paragraph(Text("L"))), Cell("tableCell", null, Paragraph(Text("P"))))));
        var nested = RichNodes.Node("bulletList", new JsonArray(RichNodes.Node("listItem", new JsonArray(Paragraph(Text("Položka")), merged))), new JsonObject { ["tight"] = false });

        var markdown = EditorMarkdownWriter.Write(Doc([table]));
        Assert.Equal("""
            ```a-table
            - cells:
                - header: true
                  content: Fáze
                - header: true
                  align: center
                  content: Popis
            - cells:
                - content: Návrh
                - content: |-
                    Body:

                    - a
                    - b
            ```
            """.Replace("\r\n", "\n", StringComparison.Ordinal), markdown);
        foreach (var block in new[] { table, nested })
        {
            var written = EditorMarkdownWriter.Write(Doc([block]));
            var read = MarkdownToRich.Parse(written, MarkdownToRich.PagePlugins, strict: true).Blocks;
            Assert.True(RichDocument.StructurallyEqual(Assert.Single(read), block), written);
            Assert.Equal(written, EditorMarkdownWriter.Write(Doc([.. read.Select(x => (JsonObject)x!)])));
        }
        // The API and MCP keep it a placeholder, as before: the merge pairs table cells by column.
        Assert.Equal("⟦c:1⟧\n", Project(table).Markdown);
    }

    [Fact]
    public void Invalid_a_table_data_is_an_issue_and_stays_code()
    {
        Assert.Equal(["Neplatná komponenta a-table: Data komponenty nemají očekávaný tvar."], StrictErrors("```a-table\n- cells:\n    - colspan: 0\n```"));
        Assert.Equal(["Neplatná komponenta a-table: Neznámý klíč rows."], StrictErrors("```a-table\n- rows: []\n```"));
        Assert.Equal("codeBlock", RichDocument.TypeOf(Assert.Single(Parse("```a-table\n- cells:\n    - align: justify\n```"))));
    }

    // ---- helpers ----

    private static List<JsonObject> Parse(string markdown)
    {
        return MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins).Blocks.Select(x => (JsonObject)x!).ToList();
    }

    private static List<string> StrictErrors(string markdown)
    {
        var error = Assert.Throws<ApiException>(() => MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true));
        Assert.Equal("invalid_document", error.Code);
        return ((List<Dictionary<string, object?>>)error.Extensions!["errors"]!).Select(x => (string)x["message"]!).ToList();
    }

    private static JsonObject Contacts(string type, params JsonObject[] contacts)
    {
        return new JsonObject
        {
            ["type"] = type,
            ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(contacts.Select(x => (JsonNode)x.DeepClone()).ToArray()) } },
        };
    }

    private static JsonObject Chart(string title, string kind, string[][] grid, JsonObject? source = null)
    {
        return new JsonObject
        {
            ["type"] = "chart",
            ["attrs"] = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["version"] = 1, ["title"] = title, ["kind"] = kind,
                    ["grid"] = new JsonArray(grid.Select(row => (JsonNode)new JsonArray(row.Select(cell => (JsonNode)cell).ToArray())).ToArray()),
                    ["source"] = source, ["sourceStatus"] = source is null ? null : "linked",
                },
            },
        };
    }

    /// <summary>Three columns; <see cref="LinkedChart"/> covers rows 1–3 and columns 2–3.</summary>
    private static JsonObject SalesTable()
    {
        static JsonObject Cell(string type, string text, string id) =>
            RichNodes.Node(type, new JsonArray(Paragraph(Text(text))), new JsonObject { ["chartRef"] = id });
        static JsonObject Row(string type, params (string Text, string Id)[] cells) =>
            RichNodes.Node("tableRow", new JsonArray(cells.Select(x => (JsonNode)Cell(type, x.Text, x.Id)).ToArray()));
        return RichNodes.Node("table", new JsonArray(
            Row("tableHeader", ("Pobočka", "h0"), ("Období", "h1"), ("Tržby", "h2")),
            Row("tableCell", ("Brno", "a0"), ("Leden", "a1"), ("12,5", "a2")),
            Row("tableCell", ("Praha", "b0"), ("Únor", "b1"), ("18", "b2"))), new JsonObject { ["chartRef"] = "tabulka-trzby" });
    }

    private static JsonObject LinkedChart()
    {
        return Chart("Tržby 2026", "line", [["Období", "Tržby"], ["Leden", "12,5"], ["Únor", "18"]],
            JsonNode.Parse("""{"tableId":"tabulka-trzby","cells":[["h1","h2"],["a1","a2"],["b1","b2"]]}""")!.AsObject());
    }

    private static ContentProjection Project(params JsonObject[] blocks)
    {
        return RichMarkdownProjection.Project(Page([.. blocks]), new ProjectionOptions(1));
    }

    private static RichDocument Page(JsonObject[] blocks, params (string Id, int Version)[] plugins)
    {
        var envelope = new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["document"] = Doc(blocks),
            ["plugins"] = new JsonArray(plugins.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["version"] = x.Version }).ToArray()),
        };
        return RichDocument.Parse(envelope.ToJsonString());
    }

    private static MergeContext Context()
    {
        var next = 0;
        return new MergeContext(_ => PageLinkResolution.None, _ => false, () => "00000000-0000-4000-8000-" + (++next).ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static JsonObject Doc(JsonObject[] blocks)
    {
        return new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(x => (JsonNode)x.DeepClone()).ToArray()) };
    }

    private static JsonObject Math(string type, string expression)
    {
        return new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["expression"] = expression } } };
    }

    private static JsonObject Diagram(string source, string caption = "")
    {
        return new JsonObject { ["type"] = "diagram", ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["source"] = source, ["caption"] = caption } } };
    }

    private static JsonObject Text(string text)
    {
        return new JsonObject { ["type"] = "text", ["text"] = text };
    }

    private static JsonObject Paragraph(params JsonObject[] inlines)
    {
        return RichNodes.Node("paragraph", new JsonArray(inlines.Select(x => (JsonNode)x).ToArray()));
    }

    private static JsonObject Heading(string text)
    {
        return RichNodes.Node("heading", new JsonArray(Text(text)), new JsonObject { ["level"] = 1, ["headingId"] = "h-fyzika" });
    }

    private static JsonObject Table(JsonObject header, JsonObject cell)
    {
        static JsonObject Row(string type, JsonObject paragraph) =>
            RichNodes.Node("tableRow", new JsonArray(RichNodes.Node(type, new JsonArray(paragraph))));
        return RichNodes.Node("table", new JsonArray(Row("tableHeader", header), Row("tableCell", cell)));
    }
}
