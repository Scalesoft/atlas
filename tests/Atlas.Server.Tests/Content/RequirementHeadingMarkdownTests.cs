using System.Globalization;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// A requirement is a heading (plan 2026-11-04-requirement-heading-shape.md §2): the attribute block on its line, the dependency line
/// under it and the facts line read and write as the editor dialect, and the round trip holds (its precondition).
/// </summary>
public sealed class RequirementHeadingMarkdownTests
{
    private const string Page = "3f1c2a4e-5b6d-4e7f-8a9b-0c1d2e3f4a5b";
    private const string Other = "7a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private static readonly string Canonical = Lf($$"""
        # Nemocnice Horní Lhota

        Běžný text nad požadavky.

        ## Identita občana

        ### Certifikát NIA {#portal.nia state=waiting_for_input kind=inbound system=NIA from={{Other}}#nia.cert stateLabel="čeká na IT"}

        od [Jana Nováková](a-contact:?name=Jana%20Nováková&objectId=6f1c1d3e-8a4b-4c55-9d1e-000000000001) · do 15. 11. 2026

        Kde uloženo: Keepass, složka NIA.

        ### Napojení na ÚZIS {#portal.uzis state=in_progress system="ÚZIS \"test\" \{B\}"}

        Závislosti: [Certifikát NIA](atlas://pages/{{Page}}?heading=portal.nia), [VPN přístup](atlas://pages/{{Other}}?heading=vpn)

        pro Nemocnici Horní Lhota

        ### {#prazdny state=planned}

        ## Rozpočet \{#2026}

        Závislosti\: jen poznámka, [odkaz](#/pages/{{Page}}?heading=portal.nia)
        """);

    /// <summary>Raw literals take the checkout's line endings; the writer always writes LF.</summary>
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public void Writing_the_parsed_dialect_gives_it_back()
    {
        Assert.Equal(Canonical, Write(Parse(Canonical)));
    }

    /// <summary>Shape §6: the party and its contact are heading attributes, written as <c>party="…" partyContact=&lt;guid&gt;</c> and read back
    /// the same; the facts line is the deadline alone.</summary>
    [Fact]
    public void The_party_and_its_contact_round_trip_as_heading_attributes()
    {
        const string Markdown = "### SSL {#portal.ssl state=planned kind=inbound party=\"Jan Novák\" partyContact=6f1c1d3e-8a4b-4c55-9d1e-000000000001}\n\ndo 15. 11. 2026";
        var blocks = Parse(Markdown);

        Assert.Equal(("Jan Novák", "6f1c1d3e-8a4b-4c55-9d1e-000000000001"), ((string?)blocks[0]["attrs"]!["party"], (string?)blocks[0]["attrs"]!["partyContactId"]));
        Assert.Equal(Markdown, Write(blocks).TrimEnd('\n'));
    }

    [Fact]
    public void Reads_requirement_headings_the_dependency_line_and_plain_text()
    {
        var blocks = Parse(Canonical);

        Assert.Equal(["heading", "paragraph", "heading", "heading", "paragraph", "paragraph", "heading", "paragraph", "paragraph", "heading", "heading", "paragraph"],
            blocks.Select(RichDocument.TypeOf));
        Assert.Equal(new JsonObject
        {
            ["level"] = 3, ["headingId"] = "portal.nia", ["state"] = "waiting_for_input", ["direction"] = "inbound", ["system"] = "NIA",
            ["from"] = Other + "#nia.cert", ["stateLabel"] = "čeká na IT",
        }.ToJsonString(), blocks[3]["attrs"]!.ToJsonString());
        Assert.Equal("Certifikát NIA", Title(blocks[3]));
        // The facts line is text with the contact node, as the dialect reads it anywhere.
        Assert.Equal(["text", "contactInline", "text"], blocks[4]["content"]!.AsArray().Select(RichDocument.TypeOf));
        Assert.Equal("ÚZIS \"test\" {B}", blocks[6]["attrs"]!["system"]!.GetValue<string>());

        var dependencies = blocks[7];
        Assert.True(RequirementHeadingMarkdown.IsDependencyLine(dependencies));
        Assert.Equal(new JsonArray(
            Reference(Page, "Certifikát NIA", "portal.nia"), new JsonObject { ["type"] = "text", ["text"] = ", " }, Reference(Other, "VPN přístup", "vpn")).ToJsonString(),
            dependencies["content"]!.ToJsonString());

        Assert.Equal(new JsonObject { ["level"] = 3, ["headingId"] = "prazdny", ["state"] = "planned" }.ToJsonString(), blocks[9]["attrs"]!.ToJsonString());
        Assert.Null(blocks[9]["content"]);
        // Escaped, the block and the label are text: a plain heading and a note with a link mark.
        Assert.Equal("Rozpočet {#2026}", Title(blocks[10]));
        Assert.Equal(new JsonObject { ["level"] = 2 }.ToJsonString(), blocks[10]["attrs"]!.ToJsonString());
        Assert.False(RequirementHeadingMarkdown.IsDependencyLine(blocks[11]));
        Assert.StartsWith("Závislosti: jen poznámka", blocks[11]["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_editors_document_reads_back_from_its_markdown()
    {
        // As the editor saves it: every default attribute present.
        var document = Doc(Parse(Canonical));
        foreach (var block in document["content"]!.AsArray().OfType<JsonObject>())
        {
            var attributes = (JsonObject)(block["attrs"] ??= new JsonObject());
            if (RichDocument.TypeOf(block) == "heading")
            {
                attributes["headingId"] ??= "h-" + attributes.Count.ToString(CultureInfo.InvariantCulture);
                foreach (var (key, fallback) in new[] { ("state", (JsonNode?)null), ("stateLabel", ""), ("direction", "internal"), ("system", ""), ("from", ""), ("aliases", ""), ("textAlign", null) })
                {
                    if (!attributes.ContainsKey(key))
                    {
                        attributes[key] = fallback?.DeepClone();
                    }
                }
            }
            else if (RichDocument.TypeOf(block) == "paragraph")
            {
                attributes["dependencies"] ??= false;
                attributes["textAlign"] = null;
            }
        }

        var parsed = Parse(EditorMarkdownWriter.Write(document));

        var stored = document["content"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(stored.Count, parsed.Count);
        for (var index = 0; index < stored.Count; index++)
        {
            Assert.True(RichDocument.StructurallyEqual(stored[index], parsed[index]), $"block {index}: {parsed[index].ToJsonString()}");
            if (stored[index]["attrs"]?["state"] is not null)
            {
                Assert.Equal(stored[index]["attrs"]!["headingId"]!.GetValue<string>(), parsed[index]["attrs"]!["headingId"]!.GetValue<string>());
            }
        }
    }

    [Fact]
    public void Loose_markdown_normalises_once()
    {
        var loose = $$"""
            ## Provoz   {state=done   #provoz.vpn kind=internal}

            Závislosti:[A](#/pages/{{Page}}?heading=a.b) ,  [B](atlas://pages/{{Page}}?heading=c)
            """;

        var written = Write(Parse(loose));

        Assert.Equal($"## Provoz {{#provoz.vpn state=done}}\n\nZávislosti: [A](atlas://pages/{Page}?heading=a.b), [B](atlas://pages/{Page}?heading=c)", written);
        Assert.Equal(written, Write(Parse(written)));
    }

    [Theory]
    [InlineData("### Úkol {#a state=hotovo}", "Stav požadavku")]
    [InlineData("### Úkol {#a kind=out}", "Druh požadavku")]
    [InlineData("### Úkol {#a from=nekde}", "Původ požadavku")]
    [InlineData("### Úkol {#a priority=1}", "Neznámý atribut")]
    [InlineData("### Úkol {#a state=done state=done}", "dvakrát")]
    [InlineData("### Úkol {#a/b}", "ID nadpisu")]
    public void An_invalid_block_is_an_error_in_strict_mode(string markdown, string message)
    {
        var error = Assert.Throws<ApiException>(() => MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true));

        Assert.Equal("invalid_document", error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        // Otherwise the line stays text.
        Assert.Contains("{#a", Title(Parse(markdown)[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("### Rozpočet {2026}")]
    [InlineData("### Rozpočet {}")]
    [InlineData("### Rozpočet{#a}")]
    [InlineData("Text {#a state=done}")]
    [InlineData("#štítek a text {#a state=hotovo}")]
    [InlineData("> ### Citace {#a state=done}")]
    [InlineData("```\n### Kód {#a state=done}\n```")]
    public void Braces_elsewhere_stay_text(string markdown)
    {
        var blocks = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true).Blocks.OfType<JsonObject>().ToList();

        Assert.DoesNotContain(blocks, x => x["attrs"]?["state"] is not null || x["attrs"]?["headingId"] is not null);
        Assert.Contains("{", EditorMarkdownWriter.Write(Doc(blocks)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("blockquote")]
    [InlineData("bulletList")]
    public void A_nested_requirement_heading_writes_no_block_and_reads_back_plain(string container)
    {
        var heading = Requirement("a", "Citace", "done");
        var nested = container == "blockquote"
            ? new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(heading) }
            : new JsonObject { ["type"] = "bulletList", ["content"] = new JsonArray(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(heading) }) };

        var written = Write([nested]);

        // Requirement attributes exist only at the top level: no attribute block, the heading reads back plain.
        Assert.EndsWith("### Citace", written, StringComparison.Ordinal);
        Assert.DoesNotContain("{", written, StringComparison.Ordinal);
        var parsed = Doc(Parse(written));
        Assert.Equal(container, parsed["content"]![0]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("\"state\"", parsed.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## Plain heading\n\nZávislosti: [A](atlas://pages/{0}?heading=a)", 1)]
    [InlineData("### Úkol {{#b state=planned}}\n\npro IT\n\nZávislosti: [A](atlas://pages/{0}?heading=a)", 2)]
    public void A_dependency_label_off_its_fixed_place_is_a_note(string markdown, int index)
    {
        // Only directly under a requirement heading is „Závislosti: …“ the dependency line (shape §1, third review P3-5).
        var blocks = Parse(string.Format(CultureInfo.InvariantCulture, markdown, Page));

        Assert.False(RequirementHeadingMarkdown.IsDependencyLine(blocks[index]));
        Assert.Null(blocks[index]["attrs"]?["dependencies"]);
        Assert.StartsWith("Závislosti:", Title(blocks[index]), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_requirements_plugin_the_dialect_is_text()
    {
        var blocks = MarkdownToRich.Parse($"### Úkol {{#a state=done}}\n\nZávislosti: [A](atlas://pages/{Page}?heading=a)").Blocks.OfType<JsonObject>().ToList();

        Assert.Equal("Úkol {#a state=done}", Title(blocks[0]));
        Assert.False(RequirementHeadingMarkdown.IsDependencyLine(blocks[1]));
    }

    [Fact]
    public void The_projection_shows_a_requirement_as_text_and_hides_an_unreadable_dependency()
    {
        var current = Stored(Requirement("portal.nia", "Certifikát NIA", "waiting_for_input"), Dependencies(Page, "portal.vpn"),
            Requirement("portal.vpn", "VPN přístup", "done"), Dependencies(Other, "vpn"));

        var projection = RichMarkdownProjection.Project(current, new ProjectionOptions(1, page => page.ToString() == Page ? "Nasazení" : null));

        Assert.Equal(Lf($$"""
            ### Certifikát NIA {#portal.nia state=waiting_for_input kind=inbound}

            Závislosti: [Cíl](atlas://pages/{{Page}}?heading=portal.vpn)

            ### VPN přístup {#portal.vpn state=done kind=inbound}

            ⟦c:1⟧
            """), projection.Markdown.TrimEnd('\n'));
        Assert.Equal(["portal.nia", "portal.vpn"], projection.Sections.Select(x => x.HeadingId));
    }

    [Fact]
    public void A_section_written_with_its_id_alone_keeps_the_requirement_and_without_a_block_is_plain()
    {
        var current = Stored(Requirement("portal.nia", "Certifikát NIA", "waiting_for_input"), Text("Poznámka."));
        var projection = RichMarkdownProjection.Project(current, new ProjectionOptions(1));

        var kept = Heading(RichMarkdownMerge.ReplaceSection(current, projection, "portal.nia", "### Certifikát NIA ověřen {#portal.nia}\n\nPoznámka.", Context()));
        var changed = Heading(RichMarkdownMerge.ReplaceSection(current, projection, "portal.nia", "### Certifikát NIA {#portal.nia state=done}\n\nPoznámka.", Context()));
        var plain = Heading(RichMarkdownMerge.ReplaceSection(current, projection, "portal.nia", "### Certifikát NIA\n\nPoznámka.", Context()));

        Assert.Equal(("portal.nia", "waiting_for_input", "inbound", "nia,3f"), Facts(kept));
        Assert.Equal(("portal.nia", "done", null, "nia,3f"), Facts(changed));
        Assert.Equal(("portal.nia", null, null, "nia,3f"), Facts(plain));
    }

    [Fact]
    public void A_written_dependency_links_only_readable_pages_and_declares_the_plugins()
    {
        var result = RichMarkdownMerge.CreateDocument(
            $"### Úkol {{#ukol.a state=planned}}\n\nZávislosti: [Skrytá](atlas://pages/{Other}?heading=x), [Cíl](atlas://pages/{Page}?heading=a.b)\n", Context());

        var blocks = RichDocument.Parse(result.ContentJson).Blocks;
        Assert.Equal(new JsonArray(Reference(Page, "Cíl", "a.b")).ToJsonString(), blocks[1]["content"]!.ToJsonString());
        Assert.Contains(result.Warnings, x => x.Contains("není dostupná", StringComparison.Ordinal));
        var plugins = JsonNode.Parse(result.ContentJson)!["plugins"]!.AsArray().Select(x => (x!["id"]!.GetValue<string>(), x["version"]!.GetValue<int>()));
        Assert.Contains(("atlas-requirements", 4), plugins);
        Assert.Contains(("heading-anchors", 2), plugins);
    }

    private static RichDocument Stored(params JsonObject[] blocks)
    {
        return RichDocument.Parse(new JsonObject
        {
            ["format"] = "atlas-rich", ["version"] = 1,
            ["plugins"] = new JsonArray(new JsonObject { ["id"] = "atlas-requirements", ["version"] = 3 }, new JsonObject { ["id"] = "heading-anchors", ["version"] = 2 },
                new JsonObject { ["id"] = "pages", ["version"] = 1 }),
            ["document"] = Doc([.. blocks]),
        }.ToJsonString());
    }

    /// <summary>A requirement heading as the editor saves it.</summary>
    private static JsonObject Requirement(string id, string title, string state)
    {
        return new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject
            {
                ["headingId"] = id, ["aliases"] = "nia,3f", ["textAlign"] = null, ["level"] = 3, ["state"] = state, ["stateLabel"] = "", ["direction"] = "inbound",
                ["system"] = "", ["from"] = "",
            },
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = title }),
        };
    }

    private static JsonObject Dependencies(string page, string heading)
    {
        return new JsonObject
        {
            ["type"] = "paragraph", ["attrs"] = new JsonObject { ["textAlign"] = null, ["dependencies"] = true },
            ["content"] = new JsonArray(Reference(page, "Cíl", heading)),
        };
    }

    private static JsonObject Text(string text)
    {
        return new JsonObject
        {
            ["type"] = "paragraph", ["attrs"] = new JsonObject { ["textAlign"] = null, ["dependencies"] = false },
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
    }

    private static JsonObject Heading(MergeResult result)
    {
        return RichDocument.Parse(result.ContentJson).Blocks.First(x => RichDocument.TypeOf(x) == "heading");
    }

    private static (string?, string?, string?, string?) Facts(JsonObject heading)
    {
        var attrs = heading["attrs"]!;
        return (attrs["headingId"]?.GetValue<string>(), attrs["state"]?.GetValue<string>(), attrs["direction"]?.GetValue<string>(), attrs["aliases"]?.GetValue<string>());
    }

    private static MergeContext Context()
    {
        var next = 0;
        return new MergeContext(_ => PageLinkResolution.None, page => page.ToString() == Page,
            () => "00000000-0000-4000-8000-" + (++next).ToString("D12", CultureInfo.InvariantCulture));
    }

    private static List<JsonObject> Parse(string markdown)
    {
        return MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins).Blocks.OfType<JsonObject>().ToList();
    }

    private static string Write(List<JsonObject> blocks)
    {
        return EditorMarkdownWriter.Write(Doc(blocks));
    }

    private static JsonObject Doc(List<JsonObject> blocks)
    {
        return new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(x => (JsonNode)x.DeepClone()).ToArray()) };
    }

    private static string Title(JsonObject block)
    {
        return string.Concat((block["content"] as JsonArray ?? []).Select(x => x?["text"]?.GetValue<string>() ?? ""));
    }

    private static JsonObject Reference(string page, string title, string heading)
    {
        return new JsonObject
        {
            ["type"] = "pageReference",
            ["attrs"] = new JsonObject { ["id"] = page, ["title"] = title, ["url"] = $"#/pages/{page}?heading={heading}" },
        };
    }

    /// <summary>
    /// Pins the line the writer gives every top-level heading of tests/fixtures/content/requirement-headings.json, and a few whose values
    /// need quoting, to tests/fixtures/content/requirement-heading-lines.json. The web's requirementHeadingMarkdown.test.ts checks that the
    /// editor writes the same line for the same heading and reads it back (its port of <see cref="RequirementHeadingMarkdown"/>).
    /// Regenerate with <c>ATLAS_UPDATE_GOLDEN=1 dotnet test --filter RequirementHeadingMarkdownTests</c>.
    /// </summary>
    [Fact]
    public async Task Heading_lines_match_the_file_the_editor_checks()
    {
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(EditorMarkdownWriterParityTests.Fixture("requirement-headings.json")))!["cases"]!.AsArray();
        var headings = cases.SelectMany(x => x!["content"]!.AsArray()).Where(x => x!["type"]!.GetValue<string>() == "heading").Select(x => x!.DeepClone()).ToList();
        static JsonNode Heading(string text, JsonObject attrs)
        {
            attrs["level"] = 2;
            return new JsonObject { ["type"] = "heading", ["attrs"] = attrs, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        }
        headings.Add(Heading("Uvozovky a mezery", new JsonObject { ["headingId"] = "q.1", ["state"] = "blocked", ["direction"] = "outbound", ["party"] = "Jan \"Honza\" Novák", ["system"] = "A {B} \\ C", ["stateLabel"] = "čeká na IT" }));
        headings.Add(Heading("Text jako blok {#x state=done}", new JsonObject { ["headingId"] = "plain.1" }));
        headings.Add(Heading("Požadavek s textem jako blok {a=b}", new JsonObject { ["headingId"] = "q.2", ["state"] = "planned" }));
        headings.Add(Heading("Dočasné ID", new JsonObject { ["headingId"] = "v3-h2", ["state"] = "done" }));
        headings.Add(Heading("Issue #", new JsonObject { ["headingId"] = "hash.1", ["state"] = "done" }));
        headings.Add(Heading("C# a F ##", new JsonObject { ["headingId"] = "hash.2" }));
        headings.Add(Heading("Složená závorka", new JsonObject { ["headingId"] = "brace.1", ["state"] = "planned", ["party"] = "Firma {CZ}", ["stateLabel"] = "čeká {na} IT" }));
        var lines = new JsonArray(headings.Select(heading => (JsonNode)new JsonObject
        {
            ["heading"] = heading.DeepClone(),
            ["markdown"] = EditorMarkdownWriter.Write(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(heading.DeepClone()) }),
        }).ToArray());
        // The reader gives the added headings back: the title as written (a trailing # stays text) and what the block says.
        foreach (var line in lines.TakeLast(7))
        {
            var written = line!["heading"]!;
            var read = MarkdownToRich.Parse(line["markdown"]!.GetValue<string>(), MarkdownToRich.PagePlugins, strict: true).Blocks.OfType<JsonObject>().Single();
            Assert.Equal(written["content"]![0]!["text"]!.GetValue<string>(), string.Concat(read["content"]!.AsArray().Select(x => x!["text"]!.GetValue<string>())));
            foreach (var name in RequirementHeadingMarkdown.Attributes)
            {
                Assert.Equal(written["attrs"]![name]?.GetValue<string>(), read["attrs"]?[name]?.GetValue<string>());
            }
        }
        var actual = lines.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        const string file = "requirement-heading-lines.json";
        if (Environment.GetEnvironmentVariable("ATLAS_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(SourceDirectory(), "..", "..", "fixtures", "content", file), actual, new System.Text.UTF8Encoding(false));
            return;
        }
        Assert.Equal((await File.ReadAllTextAsync(EditorMarkdownWriterParityTests.Fixture(file))).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path)!;
    }
}
