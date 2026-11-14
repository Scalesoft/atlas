using System.Globalization;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Atlas.Markdown.Sync;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// Markdown written back into an existing revision (plan 2026-10-05 MCP §11 K2, D8): placeholders bring back their original
/// nodes byte for byte, unchanged blocks keep their JSON, headings keep their ids, page links resolve only to readable
/// pages, and nothing Markdown could smuggle in (a password directive, an image, HTML, an unsafe link) survives as such.
/// </summary>
public sealed class RichMarkdownMergeTests
{
    private const string SecretId = "d847b3af-4a87-4e97-aab8-a74a4cf48c89";
    private static readonly Guid ReadablePage = Guid.Parse("5e940f4d-55c2-4527-addd-10f1d68d742c");
    private static readonly Guid HiddenPage = Guid.Parse("f680cde8-6cc8-4b81-a653-52bc36cd0b75");

    [Trait("Category", "Smoke")]
    [Fact]
    public void Section_edit_keeps_contact_secret_and_youtrack_nodes()
    {
        var current = Document(AccessPage());
        var projection = Project(current);
        var section = SectionMarkdown(projection, "h-pristupy");
        // The contact is the dialect's inline link (a-contact); the password references and the YouTrack issue stay placeholders.
        Assert.Equal("## Přístupy\n\nKontakt [Jan Novák](a-contact:?name=Jan%20Novák&phone=+420%20777%20123%20456) spravuje VPN.\n\n⟦c:1⟧\n\nÚkol ⟦c:2⟧ a heslo ⟦c:3⟧.\n", section);

        var edited = section.Replace("spravuje VPN.", "**spravuje** VPN i Wi-Fi.", StringComparison.Ordinal) + "\nNový odstavec.\n";
        var result = RichMarkdownMerge.ReplaceSection(current, projection, "h-pristupy", edited, Context());

        var before = current.Blocks;
        var after = Blocks(result);
        Assert.Equal(before.Count + 1, after.Count);
        // Outside the section and the unchanged blocks inside it: the stored JSON as it was.
        foreach (var index in new[] { 0, 1, 2, 4, 5 })
        {
            Assert.True(JsonNode.DeepEquals(before[index], after[index]), "block " + index);
        }
        Assert.True(JsonNode.DeepEquals(before[6], after[7]));
        Assert.True(JsonNode.DeepEquals(before[7], after[8]));
        // The edited paragraph is new text around the very same contact node.
        var paragraph = after[3]["content"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(before[3]["content"]![1], paragraph[1]));
        Assert.Equal(" ", paragraph[2]!["text"]!.GetValue<string>());
        Assert.Equal("spravuje", paragraph[3]!["text"]!.GetValue<string>());
        Assert.Equal("bold", paragraph[3]!["marks"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Nový odstavec.", after[6]["content"]![0]!["text"]!.GetValue<string>());
        // The stored Markdown is the editor's projection of the same document, password directives included.
        Assert.Equal(EditorMarkdownWriter.Write(Document(result).Document), result.Markdown);
        Assert.Contains("::secret{secretId=" + SecretId, result.Markdown, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    /// <summary>Projector's requirement blocks are components to Markdown: a section edit around them keeps them as they were (spec 2026-10-27 §2.3).</summary>
    [Fact]
    public void Section_edit_keeps_requirement_blocks_and_the_summary()
    {
        var requirement = JsonNode.Parse("""
            {"type":"requirement","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000002","key":"portal.ssl","direction":"inbound","state":"done","date":"2026-09-10",
              "party":"IT KBN","where":"Keepass","requires":""},"content":[{"type":"paragraph","content":[{"type":"text","text":"SSL certifikát"}]},
              {"type":"paragraph","content":[{"type":"text","text":"Poznámka"}]}]}
            """)!.AsObject();
        var summary = JsonNode.Parse("""{"type":"overview","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000001","kind":"requirements"}}""")!.AsObject();
        var current = Document(Heading(2, "h-nasazeni", "Nasazení"), Paragraph(Text("Před bloky.")), summary, requirement, Paragraph(Text("Za bloky.")));
        var projection = Project(current);
        var section = SectionMarkdown(projection, "h-nasazeni");
        Assert.Equal("## Nasazení\n\nPřed bloky.\n\n⟦c:1⟧\n\n⟦c:2⟧\n\nZa bloky.\n", section);
        Assert.Equal(["Souhrn nasazení", "Požadavek: SSL certifikát"], projection.Components.Select(x => x.Label));

        // Text edited right before, between and right after the blocks.
        var edited = section.Replace("Před bloky.", "Před bloky, upraveno.", StringComparison.Ordinal)
            .Replace("⟦c:1⟧\n\n", "⟦c:1⟧\n\nMezi bloky.\n\n", StringComparison.Ordinal).Replace("Za bloky.", "Za bloky, upraveno.", StringComparison.Ordinal);
        var result = RichMarkdownMerge.ReplaceSection(current, projection, "h-nasazeni", edited, Context());

        var after = Blocks(result);
        Assert.Equal(["Před bloky, upraveno.", "Mezi bloky.", "Za bloky, upraveno."], new[] { 1, 3, 5 }.Select(i => after[i]["content"]![0]!["text"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(summary, after[2]));
        Assert.True(JsonNode.DeepEquals(requirement, after[4]));
        Assert.Equal(6, after.Count);
        Assert.Contains(("atlas-requirements", 4), Plugins(result));
        Assert.Contains("- [x] **SSL certifikát** — hotovo 10. 9. 2026", result.Markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review K1: a requirement placeholder carries the block as readable text (state, direction, party, place, date, dependencies by
    /// title, notes, nested blocks), a component inside it only its label, so a model reads the block without being able to edit it.
    /// </summary>
    [Fact]
    public void Requirement_placeholders_carry_the_block_as_readable_text()
    {
        var requirement = JsonNode.Parse("""
            {"type":"requirement","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000002","key":"portal.ssl","direction":"inbound","state":"done","date":"2026-09-10",
              "party":"IT KBN","where":"Keepass › KBN","requires":""},"content":[{"type":"paragraph","content":[{"type":"text","text":"SSL certifikát"}]},
              {"type":"paragraph","content":[{"type":"text","text":"Heslo: "},{"type":"secretInlineReference","attrs":{"secretId":"6f1c1d3e-8a4b-4c55-9d1e-0000000000aa","field":"password"}}]},
              {"type":"requirement","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000003","key":"portal.sms","state":"waiting_for_input","party":"IT KBN","requires":"portal.ssl"},
               "content":[{"type":"paragraph","content":[{"type":"text","text":"SMS brána"}]},{"type":"paragraph","content":[{"type":"text","text":"Zbývá smlouva."}]}]}]}
            """)!.AsObject();
        var summary = JsonNode.Parse("""{"type":"overview","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000001","kind":"requirements"}}""")!.AsObject();
        var projection = Project(Document(summary, requirement));

        Assert.Equal("⟦c:1⟧\n\n⟦c:2⟧\n", projection.Markdown);
        Assert.Equal("**Stav nasazení:** 50 % hotovo (1 z 2) · čeká na: SMS brána (IT KBN) · čeká na vstup: SMS brána", projection.Components[0].Text);
        Assert.Equal(string.Join("\n",
            "- [x] **SSL certifikát** — hotovo 10. 9. 2026 · vstup · od: IT KBN · uloženo: Keepass › KBN · klíč: portal.ssl",
            "  Heslo: (Odkaz na heslo)",
            "  - [ ] **SMS brána** — čeká na vstup · naše práce · kdo: IT KBN · klíč: portal.sms · závisí na: SSL certifikát",
            "    Zbývá smlouva."), projection.Components[1].Text);
        Assert.Null(Project(Document(Paragraph(Text("Jen text.")), Heading(2, "h", "Nadpis"))).Components.SingleOrDefault()?.Text);
    }

    [Fact]
    public void Changed_missing_or_reordered_placeholder_is_refused()
    {
        var current = Document(AccessPage());
        var projection = Project(current);
        var section = SectionMarkdown(projection, "h-pristupy");

        foreach (var edited in new[]
        {
            // missing
            section.Replace("⟦c:1⟧\n\n", "", StringComparison.Ordinal),
            // reordered
            section.Replace("⟦c:2⟧ a heslo ⟦c:3⟧", "⟦c:3⟧ a heslo ⟦c:2⟧", StringComparison.Ordinal),
            // invented, duplicated, and a block placeholder moved into text
            section + "\nCizí ⟦c:9⟧.\n",
            section + "\nZnovu ⟦c:2⟧.\n",
            section.Replace("⟦c:1⟧\n\nÚkol", "Úkol ⟦c:1⟧ a", StringComparison.Ordinal),
        })
        {
            var error = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceSection(current, projection, "h-pristupy", edited, Context()));
            Assert.Equal((400, "components_changed"), (error.Status, error.Code));
        }
        // A new page or a new section has no component to put back.
        Assert.Equal("components_changed", Assert.Throws<ApiException>(() => RichMarkdownMerge.CreateDocument("Text ⟦c:1⟧", Context())).Code);
        Assert.Equal("components_changed", Assert.Throws<ApiException>(() =>
            RichMarkdownMerge.InsertSection(current, projection, null, SectionPosition.End, 2, "Nová", "⟦c:2⟧", Context())).Code);
    }

    [Fact]
    public void Unchanged_and_edited_headings_keep_ids()
    {
        var current = Document(
            Heading(1, "a1", "Úvod"), Paragraph(Text("A")),
            Heading(2, "b2", "Sekce"), Paragraph(Text("B")),
            Heading(3, "c3", "Pod"), Paragraph(Text("C")),
            Heading(2, null, "Stará"), Paragraph(Text("D")));
        var projection = Project(current, version: 4);
        Assert.Equal("v4-h4", projection.Sections[3].HeadingId);

        var result = RichMarkdownMerge.ReplaceSection(current, projection, "b2",
            "## Sekce přejmenovaná\n\nB\n\n### Pod\n\nC upraveno\n\n### Nová\n\nN\n", Context());

        // ?heading= on the unchanged and the renamed section still works; the new section and the legacy heading get UUIDs.
        Assert.Equal(
            [("Úvod", "a1"), ("Sekce přejmenovaná", "b2"), ("Pod", "c3"), ("Nová", NewId(1)), ("Stará", NewId(2))],
            Headings(result));

        // A legacy section is addressed by its temporary id of the projected version; the whole page keeps ids the same way.
        var legacy = RichMarkdownMerge.ReplaceSection(current, projection, "v4-h4", "## Stará a nová\n\nD\n", Context());
        Assert.Equal(("Stará a nová", NewId(1)), Headings(legacy)[^1]);
        var whole = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown.Replace("### Pod", "### Pod 2", StringComparison.Ordinal), Context());
        Assert.Equal([("Úvod", "a1"), ("Sekce", "b2"), ("Pod 2", "c3"), ("Stará", NewId(1))], Headings(whole));

        // The section must start with its heading at the same level.
        foreach (var edited in new[] { "", "Jen text\n", "### Sekce\n\nB\n" })
        {
            var error = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceSection(current, projection, "b2", edited, Context()));
            Assert.Equal((400, "invalid_markdown"), (error.Status, error.Code));
        }
        Assert.Equal(404, Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceSection(current, projection, "zz", "## X\n", Context())).Status);
        // A temporary id of another version (the page moved on since the read) is a bad argument with the way out, not a missing section.
        var stale = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceSection(current, projection, "v3-h4", "## Stará\n", Context()));
        Assert.Equal((400, "invalid_argument"), (stale.Status, stale.Code));
        Assert.Contains("atlas_get_page", stale.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_directives_images_html_and_unsafe_links_are_refused_or_neutralized()
    {
        foreach (var markdown in new[]
        {
            "::secret{secretId=" + SecretId + "}",
            "Heslo :secret{secretId=" + SecretId + " field=password}",
            "Heslo &#58;secret{secretId=" + SecretId + "}",
            "```\n::secret{secretId=" + SecretId + "}\n```",
        })
        {
            var error = Assert.Throws<ApiException>(() => RichMarkdownMerge.CreateDocument(markdown, Context()));
            Assert.Equal((400, "secret_directive_forbidden"), (error.Status, error.Code));
        }

        var result = RichMarkdownMerge.CreateDocument(string.Join("\n\n",
            "![logo](https://evil.example/pixel.png)",
            "<img src=x onerror=alert(1)>",
            "Text <b>tučně</b>",
            "[klik](javascript:alert(1)) a [data](data:text/html,x)",
            $"[stránka](atlas://pages/{ReadablePage}?heading=h-1) a [skrytá](atlas://pages/{HiddenPage}) a [jiná](#/pages/{Guid.NewGuid()})"), Context());

        var json = result.ContentJson;
        Assert.DoesNotContain("\"image\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("data:text", json, StringComparison.Ordinal);
        Assert.DoesNotContain(HiddenPage.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", result.Markdown, StringComparison.Ordinal);
        var blocks = Blocks(result);
        // The image is a plain link to its address; HTML is text; the readable Atlas page is a stored page link.
        Assert.Equal("https://evil.example/pixel.png", blocks[0]["content"]![0]!["marks"]![0]!["attrs"]!["href"]!.GetValue<string>());
        Assert.Equal("<img src=x onerror=alert(1)>", blocks[1]["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains($"\"href\":\"#/pages/{ReadablePage}?heading=h-1\"", json, StringComparison.Ordinal);
        Assert.Equal(
        [
            "Obrázek z adresy mimo přílohy stránky je převedený na odkaz; vložit lze jen obrázek nahraný ke stránce: ![popis](/api/attachments/{id}/content).",
            "HTML je převedené na text.",
            "Odkaz s nepovoleným schématem je převedený na text.",
            "Odkaz na stránku nebyl převeden: stránka není dostupná.",
        ], result.Warnings);
    }

    [Fact]
    public void Wiki_links_resolve_uniquely_or_warn()
    {
        var result = RichMarkdownMerge.CreateDocument("Viz [[Provoz]], [[Porada]], [[Tajný projekt]] a [[Neexistuje]], ne \\[\\[Provoz\\]\\].", Context());

        var content = Blocks(result)[0]["content"]!.AsArray();
        Assert.Equal(3, content.Count);
        // The same node the editor inserts for [[: id, readable title and the page URL.
        Assert.True(JsonNode.DeepEquals(new JsonObject
        {
            ["type"] = "pageReference",
            ["attrs"] = new JsonObject { ["id"] = ReadablePage.ToString(), ["title"] = "Provoz", ["url"] = "#/pages/" + ReadablePage },
        }, content[1]));
        Assert.Equal(", [[Porada]], [[Tajný projekt]] a [[Neexistuje]], ne [[Provoz]].", content[2]!["text"]!.GetValue<string>());
        // An unreadable page and a missing one read the same.
        Assert.Equal(
        [
            "Odkaz [[Porada]] nebyl převeden: názvu odpovídá více stránek.",
            "Odkaz [[Tajný projekt]] nebyl převeden: stránka nebyla nalezena.",
            "Odkaz [[Neexistuje]] nebyl převeden: stránka nebyla nalezena.",
        ], result.Warnings);
        Assert.Contains(Plugins(result), plugin => plugin == ("pages", 1));
    }

    [Fact]
    public void Oversized_markdown_is_refused_before_parsing()
    {
        var current = Document(Paragraph(Text("A")));
        var huge = new string('a', 1_000_001);
        var error = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(current, Project(current), huge, Context()));
        Assert.Equal((413, "invalid_markdown"), (error.Status, error.Code));
    }

    [Fact]
    public void A_whole_page_replacement_removes_only_the_components_it_lists_and_refuses_foreign_ones()
    {
        var current = Document(AccessPage());
        var projection = Project(current);
        Assert.Equal([1, 2, 3], projection.Components.Select(x => x.Index));
        // Sent back unchanged it is the stored page.
        Assert.True(JsonNode.DeepEquals(current.Document, JsonNode.Parse(RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown, Context()).ContentJson)!["document"]));

        // Left out without being listed: refused, naming each component with its label.
        var unlisted = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(current, projection, "Text", Context(), [1, 3]));
        Assert.Equal((409, "components_removed"), (unlisted.Status, unlisted.Code));
        Assert.Contains("⟦c:2⟧ (Úkol YouTrack: ABC-12).", unlisted.Message, StringComparison.Ordinal);
        Assert.Equal([2], (IEnumerable<int>)unlisted.Extensions!["components"]!);

        // Listed: removed and named.
        var removed = RichMarkdownMerge.ReplaceAll(current, projection, "Text", Context(), [1, 2, 3]);
        Assert.Equal("Odebrané komponenty: " + string.Join(", ", projection.Components.Select(x => MarkdownToRich.Placeholder(x.Index) + " (" + x.Label + ")")) + ".",
            removed.Warnings[0]);
        Assert.Equal("Nadpisy s ID h-projekt, h-pristupy, h-dalsi už na stránce nejsou; odkazy ?heading= na ně nepovedou.", removed.Warnings[1]);
        Assert.All(Blocks(removed), x => Assert.Equal("paragraph", x["type"]!.GetValue<string>()));

        // A listed placeholder the Markdown still holds, or one the page does not have; foreign and repeated placeholders.
        Assert.Equal((400, "invalid_argument"), Code(() => RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown, Context(), [2])));
        Assert.Equal((400, "invalid_argument"), Code(() => RichMarkdownMerge.ReplaceAll(current, projection, "Text", Context(), [1, 2, 3, 9])));
        // A client's list of any length is checked in one pass and named only in part (S-H2).
        var many = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(current, projection, "Text", Context(), Enumerable.Range(1, 300_000).ToArray()));
        Assert.EndsWith("⟦c:23⟧ a dalších 299977 na stránce není.", many.Message, StringComparison.Ordinal);
        Assert.Equal((400, "components_changed"), Code(() => RichMarkdownMerge.ReplaceAll(current, projection, "⟦c:99⟧", Context(), [1, 2, 3])));
        Assert.Equal((400, "components_changed"), Code(() => RichMarkdownMerge.ReplaceAll(current, projection, "⟦c:1⟧\n\n⟦c:1⟧", Context(), [2, 3])));
    }

    [Fact]
    public void A_formatted_page_sent_back_unchanged_is_stored_as_it_was_and_an_edit_keeps_what_markdown_does_not_carry()
    {
        var current = RichDocument.Parse(FormattedPage);
        var projection = Project(current);
        // Alignment, cell colours and widths, row heights and a list's numbering style do not hide the text behind placeholders.
        Assert.Empty(projection.Components);
        // Written as the web stores it: Czech letters raw, not escaped (D-M2).
        var same = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown, Context()).ContentJson;
        Assert.Equal(JsonNode.Parse(FormattedPage)!.ToJsonString(RichDocument.JsonWrite), same);
        Assert.Contains("Ověřit", same, StringComparison.Ordinal);

        // One sentence of the centred paragraph, one cell, one list item and the heading's text change: each keeps its look.
        var edits = new[] { ("Postup", "Postup zapnutí"), ("První věta.", "Jediná věta."), ("5 min", "10 min"), ("Ověřit", "Ověřit stav") };
        var markdown = edits.Aggregate(projection.Markdown, (text, edit) => text.Replace(edit.Item1, edit.Item2, StringComparison.Ordinal));
        var result = RichMarkdownMerge.ReplaceAll(current, projection, markdown, Context());

        var expected = RichDocument.Parse(edits.Aggregate(FormattedPage, (text, edit) => text.Replace(edit.Item1, edit.Item2, StringComparison.Ordinal)));
        Assert.True(RichDocument.StructurallyEqual(expected.Document, Document(result).Document), result.ContentJson);
        Assert.Equal([("Postup zapnutí", "h-postup")], Headings(result));
        // The link keeps what the editor's defaults would have replaced (rel and class are not kept since Atlas 4.0: LegacyDocumentRepair).
        var link = Blocks(result)[1]["content"]![2]!["marks"]![0]!["attrs"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"href":"https://example.com/navod","title":"Návod","target":null}"""), link),
            link.ToJsonString());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Appended_text_follows_the_page_and_keeps_its_components()
    {
        var current = Document(AccessPage());
        var projection = Project(current);
        var result = RichMarkdownMerge.Append(current, projection, "## Doplněk\n\nNový text.", Context());

        var before = current.Blocks;
        var after = Blocks(result);
        // Every stored block, components included, stays as it was; the new section comes before the closing spacing paragraph.
        for (var index = 0; index < before.Count - 1; index++)
        {
            Assert.True(JsonNode.DeepEquals(before[index], after[index]), "block " + index);
        }
        Assert.Equal(("Doplněk", NewId(1)), Headings(result)[^1]);
        Assert.Equal("Nový text.", after[^2]["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("paragraph", after[^1]["type"]!.GetValue<string>());
        Assert.Null(after[^1]["content"]);
        Assert.Equal("components_changed", Assert.Throws<ApiException>(() => RichMarkdownMerge.Append(current, projection, "Text ⟦c:1⟧", Context())).Code);
    }

    [Theory]
    [InlineData("==***a***b==", "***==a==***==b==")]
    [InlineData("==*<http://g>*.==", "[*==http://g==*](http://g)==.==")]
    public void A_mark_that_outlives_two_marks_opened_before_it_is_written_as_the_editor_writes_it(string markdown, string expected)
    {
        // Three marks on the first text, only the last of them on the next (tests/fixtures/content/mark-order.json): the writer
        // used to throw on its way through the merge, so every MCP draft tool answered such Markdown with internal_error.
        Assert.Equal(expected, RichMarkdownMerge.CreateDocument(markdown, Context()).Markdown);
    }

    /// <summary>
    /// Writes tests/fixtures/content/expected/merge.json: the documents the merge produces for a section edit with every
    /// component, an inserted section, a new page and a replaced page. The web golden test opens them in the editor pinned
    /// in 2.0, which must keep the JSON and write exactly the Markdown the server stored.
    /// </summary>
    [Fact]
    public async Task Server_pair_round_trips_in_the_editor()
    {
        var current = Document(ComponentPage());
        var projection = Project(current);
        var section = SectionMarkdown(projection, "h-komponenty");
        var edited = RichMarkdownMerge.ReplaceSection(current, projection, "h-komponenty",
            section.Replace("Kontakt", "Kontaktní osoba", StringComparison.Ordinal) + "\n- [x] hotovo\n- [ ] zbývá\n", Context());
        var inserted = RichMarkdownMerge.InsertSection(current, projection, "h-komponenty", SectionPosition.AfterSection, 2, "Postup **nasazení**",
            "1. Sestavit\n2. Ověřit\n\n> [!WARNING]\n> Jen po schválení.\n\n| Krok | Kdo |\n| :--- | ---: |\n| A | [[Provoz]] |\n\n```sh\nmake deploy\n```", Context());
        var created = RichMarkdownMerge.CreateDocument(
            "# Nová stránka\n\nOdkaz na [[Provoz]] a [web](https://example.com/a_b) s ==zvýrazněním== a `kódem`.\n\n## Část\n\n> Citace\n> na dva řádky\n\n---\n\n* jedna\n* dvě\n", Context());
        var text = Document(Heading(1, "t1", "Text"), Paragraph(Text("Původní odstavec.")), Heading(2, "t2", "Druhá"), Paragraph(Text("Konec.")));
        var textProjection = Project(text);
        var replaced = RichMarkdownMerge.ReplaceAll(text, textProjection,
            textProjection.Markdown.Replace("Původní odstavec.", "Upravený *odstavec*.", StringComparison.Ordinal) + "\n### Doplněk\n\nText.\n", Context());

        // Formulas and diagrams the merge reads from Markdown (its input in "source"): the web test also reads that input with the
        // editor's own Markdown reader and expects the same document.
        var formulas = """
            Vzorec $E = mc^2$, **$$a + b$$** a [odkaz $x$](https://example.com).

            Ceny $5 a $10.

            Cesta $HOME/$USER.

            Mezera $ x$ a $y $.

            Znak \$x$.

            $$
            \int_0^1 x\,dx
            $$

            $$x^2$$ a text

            $$x^3$$

            $$
            a + b

            c
            $$

            > $$
            > q
            > $$

            - položka

              $$
              r
              $$
            - další

            ```math
            a

            b
            ```

            ```math
            ```

            | Norma | Jiný |
            | --- | --- |
            | $\|x\|$ | $y$ |

            ```mermaid
            flowchart LR
              A --> B
            ```

            Odstavec
            $$
            z
            $$
            a $$u
            v$$ konec.

            | Buňka | Druhá | Třetí |
            | --- | --- | --- |
            | $a|b$ | c |

            Konec $$
            w

            ```mermaid
            A[<b>x</b>]
            ```

            ~~~ Mermaid
            %%{init: {}}%%
            A
            ~~~
            """.ReplaceLineEndings("\n");
        // Contacts and charts of the dialect (a-contact, a-chart, a table's {#id}, the reserved a- namespace), read the same way.
        var dialect = """
            Volejte [Eva \[Malá\]](a-contact:?name=Eva%20[Malá]&phone=+420%20111&phone=222&email=eva@x.cz&address=A%26B%0AC&internalUser.id=u1) nebo **[kohokoli](a-contact:?name=Petr)**.

            ```a-contact
            # komentář
            - name: 2026
              title: 1.50
              organization: "true"
              phones: [+420777123456, 0042 1]
              emails:
              - jana@x.cz
              address: |
                Náměstí 1

                Brno
              kind: internal
              internalUser:
                id: 007
                name: Jana N.
            - name: Petr
            ```

            > | Pobočka | Období | Tržby |
            > | --- | --- | --- |
            > | Brno | **Leden** | 12,5 |
            > | Praha | Únor | 18 |
            >
            > {#tabulka-trzby}

            ```a-chart
            title: Tržby 2026
            kind: line
            source:
              table: tabulka-trzby
              rows: 1-3
              columns: 2-3
            ---
            | Období | Tržby |
            | --- | --- |
            | Leden | 99 |
            | Únor | 18 |
            ```

            ~~~a-chart
            ---
            Kategorie | A\|B
            :--- | ---:
            C:\\data\\n | 1
            dva\nřádky | |
            ~~~

            | a | b |
            | --- | --- |
            | x | 1 |

            {#nikdo}

            ```a-code a-chart
            title: x
            ---
            ```

            ```a-code mermaid
            A --> B
            ```
            """.ReplaceLineEndings("\n");
        var golden = new JsonArray(new (string, MergeResult, string?)[]
            {
                ("section-with-components", edited, null), ("inserted-section", inserted, null), ("created-page", created, null), ("replaced-page", replaced, null),
                ("formulas-and-diagrams", RichMarkdownMerge.CreateDocument(formulas, Context()), formulas),
                ("contacts-and-charts", RichMarkdownMerge.CreateDocument(dialect, Context()), dialect),
            }
            .Select(x =>
            {
                var page = new JsonObject
                {
                    ["name"] = x.Item1,
                    ["content"] = JsonNode.Parse(x.Item2.ContentJson),
                    ["markdown"] = x.Item2.Markdown,
                };
                if (x.Item3 is not null)
                {
                    page["source"] = x.Item3;
                }
                return (JsonNode)page;
            }).ToArray());
        await EditorMarkdownWriterParityTests.AssertGoldenAsync("merge.json", golden);
    }

    [Fact]
    public void A_new_row_and_a_split_paragraph_keep_the_look_and_a_look_with_nowhere_to_go_is_refused_until_reset()
    {
        var current = RichDocument.Parse(FormattedPage);
        var projection = Project(current);
        Assert.Contains("| Krok | Čas |\n| --- | --- |\n| Zapnout | 5 min |\n", projection.Markdown, StringComparison.Ordinal);

        // A row added to the coloured table and the centred paragraph split in two: nothing is lost, the new row has the defaults.
        var grown = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown
            .Replace("| Zapnout | 5 min |", "| Zapnout | 5 min |\n| Vypnout | 1 min |", StringComparison.Ordinal)
            .Replace("První věta. Druhá", "První věta.\n\nDruhá", StringComparison.Ordinal), Context());
        var blocks = Blocks(grown);
        Assert.Equal(["center", "center"], blocks.Skip(1).Take(2).Select(x => x["attrs"]!["textAlign"]!.GetValue<string>()));
        var rows = blocks[4]["content"]!.AsArray();
        Assert.Equal(48, rows[1]!["attrs"]!["height"]!.GetValue<int>());
        Assert.Equal(["accent", null, "green", "amber", null, null],
            rows.SelectMany(x => x!["content"]!.AsArray()).Select(x => x!["attrs"]?["backgroundColor"]?.GetValue<string>()));
        // The width is the column's: the new row's cell has it too, as the editor's table geometry wants.
        Assert.Equal("[160]", rows[2]!["content"]![0]!["attrs"]!["colwidth"]!.ToJsonString());
        Assert.Null(rows[2]!["attrs"]?["height"]);

        // One column dropped while the other is renamed: which of them went is not clear, so neither column's look has a place.
        var narrowed = projection.Markdown.Replace("| Krok | Čas |\n| --- | --- |\n| Zapnout | 5 min |", "| Krok a čas |\n| --- |\n| Zapnout |", StringComparison.Ordinal);
        var refused = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(current, projection, narrowed, Context()));
        Assert.Equal((409, "formatting_lost"), (refused.Status, refused.Code));
        Assert.Contains("tabulka (4. blok sekce h-postup: barva buněk, šířka sloupců)", refused.Message, StringComparison.Ordinal);
        Assert.Equal("""[{"headingId":"h-postup","block":4,"type":"table","attributes":["backgroundColor","colwidth"]}]""",
            System.Text.Json.JsonSerializer.Serialize(refused.Extensions!["blocks"]));
        // Asked for, the edit goes through with the cells' defaults; the rows, edited in place, keep their height.
        var reset = Blocks(RichMarkdownMerge.ReplaceAll(current, projection, narrowed, Context(), resetFormatting: true))[3]["content"]!.AsArray();
        Assert.Null(reset[1]!["content"]![0]!["attrs"]?["backgroundColor"]);
        Assert.Equal(48, reset[1]!["attrs"]!["height"]!.GetValue<int>());
    }

    [Fact]
    public void A_row_inserted_beside_an_edited_row_is_refused_and_inserted_alone_keeps_the_colours_on_their_rows()
    {
        static string Row(string cell, string text, string? colour) =>
            "{\"type\":\"tableRow\",\"content\":[{\"type\":\"" + cell + "\",\"attrs\":{\"backgroundColor\":" + (colour is null ? "null" : "\"" + colour + "\"")
            + "},\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}]}";
        var json = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"table","content":["""
            + string.Join(",", Row("tableHeader", "Krok", null), Row("tableCell", "A", "green"), Row("tableCell", "B", "amber"), Row("tableCell", "C", null))
            + """]},{"type":"paragraph"}]},"plugins":[]}""";
        // The fixture is a document the editor opens, not a hand-shaped approximation.
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(json));
        var current = RichDocument.Parse(json);
        var projection = Project(current);
        Assert.Equal("| Krok |\n| --- |\n| A |\n| B |\n| C |\n", projection.Markdown);
        static IEnumerable<string?> Colours(MergeResult result)
            => Blocks(result)[0]["content"]!.AsArray().Select(x => x!["content"]![0]!["attrs"]?["backgroundColor"]?.GetValue<string>());

        // Inserted alone: the unchanged rows are found by their content and keep their colours.
        var inserted = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown.Replace("| B |", "| X |\n| B |", StringComparison.Ordinal), Context());
        Assert.Equal([null, "green", null, "amber", null], Colours(inserted));

        // Inserted beside an edited row: which new row continues B is not clear, so its colour is not moved but refused.
        var shifted = projection.Markdown.Replace("| B |", "| X |\n| B upraveno |", StringComparison.Ordinal);
        Assert.Equal((409, "formatting_lost"), Code(() => RichMarkdownMerge.ReplaceAll(current, projection, shifted, Context())));
        Assert.Equal([null, "green", null, null, null], Colours(RichMarkdownMerge.ReplaceAll(current, projection, shifted, Context(), resetFormatting: true)));
    }

    [Fact]
    public void Swapped_sections_keep_their_heading_ids_and_look_and_an_edited_moved_block_is_a_formatting_loss()
    {
        var current = Document(
            Aligned(Heading(2, "h-a", "A"), "right"), Aligned(Paragraph(Text("Text A.")), "center"),
            Heading(2, "h-b", "B"), Aligned(Paragraph(Text("Text B.")), "right"));
        var projection = Project(current);
        Assert.Equal("## A\n\nText A.\n\n## B\n\nText B.\n", projection.Markdown);

        // Moved as they are: every block is the stored one, so ?heading= links and alignment stay with their text.
        var swapped = RichMarkdownMerge.ReplaceAll(current, projection, "## B\n\nText B.\n\n## A\n\nText A.\n", Context());
        Assert.Equal([("B", "h-b"), ("A", "h-a")], Headings(swapped));
        Assert.Equal([null, "right", "right", "center"], Blocks(swapped).Select(x => x["attrs"]?["textAlign"]?.GetValue<string>()));
        Assert.Empty(swapped.Warnings);

        // Moved and edited: the centred paragraph cannot be told from a new one, so its look is refused, not dropped.
        var edited = "## B\n\nText B.\n\n## A\n\nText A upravený.\n";
        Assert.Equal((409, "formatting_lost"), Code(() => RichMarkdownMerge.ReplaceAll(current, projection, edited, Context())));
        Assert.Equal([("B", "h-b"), ("A", "h-a")], Headings(RichMarkdownMerge.ReplaceAll(current, projection, edited, Context(), resetFormatting: true)));
    }

    [Fact]
    public void A_heading_in_a_quote_keeps_its_id_when_its_neighbour_is_edited()
    {
        var quote = new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(Heading(3, "h-citace", "V citaci"), Paragraph(Text("Původní."))) };
        var current = Document(Heading(2, "h-a", "A"), quote, Paragraph());
        var projection = Project(current);
        Assert.Contains("> ### V citaci\n>\n> Původní.", projection.Markdown, StringComparison.Ordinal);

        var result = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown.Replace("Původní.", "Upravený.", StringComparison.Ordinal), Context());
        Assert.Equal("h-citace", Blocks(result)[1]["content"]![0]!["attrs"]!["headingId"]!.GetValue<string>());
    }

    [Fact]
    public void Heading_ids_that_disappear_or_pass_to_another_heading_are_warned_about()
    {
        var current = Document(Heading(2, "h-a", "A"), Paragraph(Text("Text A.")), Heading(2, "h-b", "B"), Paragraph(Text("Text B.")));
        var projection = Project(current);

        // A level change gives the heading a new id (D8): the old one is named.
        var leveled = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown.Replace("## B", "### B", StringComparison.Ordinal), Context());
        Assert.Equal([("A", "h-a"), ("B", NewId(1))], Headings(leveled));
        Assert.Equal(["Nadpisy s ID h-b už na stránce nejsou; odkazy ?heading= na ně nepovedou."], leveled.Warnings);

        // A heading inserted beside an edited one takes its id by level and order.
        var inserted = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown.Replace("## A", "## Nový\n\n## A upravený", StringComparison.Ordinal), Context());
        Assert.Equal([("Nový", "h-a"), ("A upravený", NewId(1)), ("B", "h-b")], Headings(inserted));
        Assert.Equal(["ID nadpisů h-a převzaly podle úrovně a pořadí upravené nadpisy; ověřte, že odkazy ?heading= vedou na správné místo."], inserted.Warnings);
    }

    [Fact]
    public void Many_headings_changing_level_are_paired_in_bounded_time()
    {
        // Every heading a level lower: none pairs with a stored one, so each would scan all of them (quadratic, about 17 s here).
        const int count = 15000;
        var current = Document([.. Enumerable.Range(0, count).Select(x => Heading(2, "h-" + x, "H" + x)), Paragraph()]);
        var projection = Project(current);
        var edited = string.Concat(Enumerable.Range(0, count).Select(x => "### H" + x + "\n\n"));

        List<(string Title, string Id)> headings = [];
        var spent = ThreadCpuTime.Of(() => headings = Headings(RichMarkdownMerge.ReplaceAll(current, projection, edited, Context())));

        Assert.Equal([("H0", NewId(1)), ("H14999", NewId(count))], new[] { headings[0], headings[^1] });
        // This thread's processor time, not the wall clock a busy machine stretches (V6); the linear pairing takes well under a second.
        Assert.True(spent < TimeSpan.FromSeconds(5), spent.ToString());
    }

    [Fact]
    public void Links_to_one_address_keep_their_own_attributes_and_a_block_changed_into_another_kind_reports_them()
    {
        static JsonObject Link(string text, string? target) => new()
        {
            ["type"] = "text", ["text"] = text,
            ["marks"] = new JsonArray(new JsonObject
            {
                ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = "https://example.com/a", ["target"] = target },
            }),
        };
        var current = Document(Heading(2, "h-a", "A"), Paragraph(Text("Viz "), Link("první", null), Text(" a "), Link("druhý", "_blank"), Text(".")), Paragraph());
        var projection = Project(current);

        // Edited around them, each link keeps its own target, in order.
        var edited = RichMarkdownMerge.ReplaceAll(current, projection, projection.Markdown.Replace("Viz ", "Podívejte se na ", StringComparison.Ordinal), Context());
        var targets = Blocks(edited)[1]["content"]!.AsArray().Where(x => x!["marks"] is not null)
            .Select(x => x!["marks"]![0]!["attrs"]!["target"]?.GetValue<string>() ?? "in place");
        Assert.Equal(["in place", "_blank"], targets);

        // The paragraph changed into a heading: its links' attributes have nowhere to go.
        var heading = projection.Markdown.Replace("Viz ", "## Viz ", StringComparison.Ordinal);
        var refused = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(current, projection, heading, Context()));
        Assert.Equal((409, "formatting_lost"), (refused.Status, refused.Code));
        Assert.Contains("odstavec (2. blok sekce h-a: cíl a třída odkazů)", refused.Message, StringComparison.Ordinal);
    }

    // ---- documents ----

    /// <summary>
    /// A page as the web editor stores it, with everything Markdown cannot carry: a right-aligned heading, a centred paragraph with a
    /// link of its own target, rel and class, a lettered list with an aligned item, a table with cell colours, column widths and a
    /// row height. Shared with the HTTP and MCP tests of the same rule.
    /// </summary>
    internal const string FormattedPage = """
        {"format":"atlas-rich","version":1,"document":{"type":"doc","content":[
        {"type":"heading","attrs":{"headingId":"h-postup","textAlign":"right","level":2},"content":[{"type":"text","text":"Postup"}]},
        {"type":"paragraph","attrs":{"textAlign":"center"},"content":[{"type":"text","marks":[{"type":"bold"}],"text":"Pozor:"},{"type":"text","text":" První věta. Druhá věta s "},{"type":"text","marks":[{"type":"link","attrs":{"href":"https://example.com/navod","target":null,"title":"Návod"}}],"text":"návodem"},{"type":"text","text":"."}]},
        {"type":"orderedList","attrs":{"tight":true,"start":1,"type":"a"},"content":[
        {"type":"listItem","content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"Zapnout"}]}]},
        {"type":"listItem","content":[{"type":"paragraph","attrs":{"textAlign":"right"},"content":[{"type":"text","text":"Ověřit"}]}]}]},
        {"type":"table","attrs":{"chartRef":null},"content":[
        {"type":"tableRow","attrs":{"height":null,"chartRef":null},"content":[
        {"type":"tableHeader","attrs":{"textAlign":null,"backgroundColor":"accent","chartRef":null,"colspan":1,"rowspan":1,"colwidth":[160],"align":null},"content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"Krok"}]}]},
        {"type":"tableHeader","attrs":{"textAlign":null,"backgroundColor":null,"chartRef":null,"colspan":1,"rowspan":1,"colwidth":null,"align":null},"content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"Čas"}]}]}]},
        {"type":"tableRow","attrs":{"height":48,"chartRef":null},"content":[
        {"type":"tableCell","attrs":{"textAlign":null,"backgroundColor":"green","chartRef":null,"colspan":1,"rowspan":1,"colwidth":[160],"align":null},"content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"Zapnout"}]}]},
        {"type":"tableCell","attrs":{"textAlign":null,"backgroundColor":"amber","chartRef":null,"colspan":1,"rowspan":1,"colwidth":null,"align":null},"content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"5 min"}]}]}]}]},
        {"type":"paragraph","attrs":{"textAlign":null}}]},
        "plugins":[{"id":"charts","version":1},{"id":"heading-anchors","version":1}]}
        """;

    private static JsonObject[] AccessPage()
    {
        return
        [
            Heading(1, "h-projekt", "Projekt"),
            Paragraph(Text("Úvod.")),
            Heading(2, "h-pristupy", "Přístupy"),
            Paragraph(Text("Kontakt "), ContactInline("Jan Novák"), Text(" spravuje VPN.")),
            SecretBlock(),
            Paragraph(Text("Úkol "), YouTrack(), Text(" a heslo "), SecretInline(), Text(".")),
            Heading(2, "h-dalsi", "Další"),
            Paragraph(Text("Konec.")),
            Paragraph(),
        ];
    }

    private static JsonObject[] ComponentPage()
    {
        return
        [
            Heading(1, "h-uvod", "Úvod"),
            Paragraph(Text("Stránka se všemi komponentami.")),
            Heading(2, "h-komponenty", "Komponenty"),
            Paragraph(Text("Kontakt "), ContactInline("Jan Novák"), Text(", úkol "), YouTrack(), Text(", heslo "), SecretInline(), Text(", stránka "),
                new JsonObject { ["type"] = "pageReference", ["attrs"] = new JsonObject { ["id"] = ReadablePage.ToString(), ["title"] = "Provoz", ["url"] = "#/pages/" + ReadablePage } },
                Text(" a "), new JsonObject { ["type"] = "emoji", ["attrs"] = new JsonObject { ["name"] = "rocket" } }),
            Module("contactCard", new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = "Eva Malá", ["emails"] = new JsonArray("eva@example.com") }) }),
            SecretBlock(),
            Module("attachment", new JsonObject { ["version"] = 1, ["name"] = "smlouva.pdf", ["url"] = "/api/attachments/0b6b1c38-6b8c-4d9f-9d1e-1b2c3d4e5f60/download", ["size"] = 2048 }),
            Module("chart", new JsonObject
            {
                ["version"] = 1, ["title"] = "Tržby", ["kind"] = "bar",
                ["grid"] = new JsonArray(new JsonArray("Měsíc", "Tržby"), new JsonArray("Leden", "12")), ["source"] = null, ["sourceStatus"] = null,
            }),
            Paragraph(Text("Konec sekce.")),
            Heading(2, "h-zaver", "Závěr"),
            Paragraph(Text("Hotovo.")),
            Paragraph(),
        ];
    }

    // ---- helpers ----

    private static MergeContext Context()
    {
        var next = 0;
        return new MergeContext(
            title => title switch
            {
                "Provoz" => PageLinkResolution.Unique(ReadablePage, "Provoz"),
                "Porada" => PageLinkResolution.Ambiguous,
                _ => PageLinkResolution.None,
            },
            page => page == ReadablePage,
            () => NewId(++next));
    }

    private static string NewId(int number)
    {
        return "00000000-0000-4000-8000-" + number.ToString("D12", CultureInfo.InvariantCulture);
    }

    private static RichDocument Document(params JsonObject[] blocks)
    {
        var envelope = new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(x => (JsonNode)x.DeepClone()).ToArray()) },
            ["plugins"] = new JsonArray(
                new JsonObject { ["id"] = "contacts", ["version"] = 2 }, new JsonObject { ["id"] = "heading-anchors", ["version"] = 2 },
                new JsonObject { ["id"] = "secrets", ["version"] = 4 }, new JsonObject { ["id"] = "youtrack", ["version"] = 1 }),
        };
        return RichDocument.Parse(envelope.ToJsonString());
    }

    private static RichDocument Document(MergeResult result)
    {
        return RichDocument.Parse(result.ContentJson);
    }

    private static ContentProjection Project(RichDocument document, int version = 1)
    {
        return RichMarkdownProjection.Project(document, new ProjectionOptions(version, page => page == ReadablePage ? "Provoz" : null));
    }

    private static IReadOnlyList<JsonObject> Blocks(MergeResult result)
    {
        return Document(result).Blocks;
    }

    private static string SectionMarkdown(ContentProjection projection, string headingId)
    {
        var section = projection.Sections.Single(x => x.HeadingId == headingId);
        var lines = projection.Markdown.Split('\n');
        return string.Join("\n", lines[(section.StartLine - 1)..section.EndLine]) + "\n";
    }

    private static List<(string Title, string Id)> Headings(MergeResult result)
    {
        return Blocks(result).Where(x => x["type"]!.GetValue<string>() == "heading")
            .Select(x => (string.Concat(x["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>() ?? "")), x["attrs"]!["headingId"]!.GetValue<string>()))
            .ToList();
    }

    private static List<(string Id, int Version)> Plugins(MergeResult result)
    {
        return JsonNode.Parse(result.ContentJson)!["plugins"]!.AsArray()
            .Select(x => (x!["id"]!.GetValue<string>(), x["version"]!.GetValue<int>())).ToList();
    }

    private static (int, string?) Code(Action action)
    {
        var error = Assert.Throws<ApiException>(action);
        return (error.Status, error.Code);
    }

    private static JsonObject Text(string text)
    {
        return new JsonObject { ["type"] = "text", ["text"] = text };
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

    private static JsonObject Heading(int level, string? id, string text)
    {
        var attributes = new JsonObject { ["level"] = level };
        if (id is not null)
        {
            attributes["headingId"] = id;
        }
        return new JsonObject { ["type"] = "heading", ["attrs"] = attributes, ["content"] = new JsonArray(Text(text)) };
    }

    private static JsonObject Aligned(JsonObject block, string align)
    {
        var attributes = block["attrs"] as JsonObject ?? [];
        attributes["textAlign"] = align;
        block["attrs"] = attributes;
        return block;
    }

    private static JsonObject Module(string type, JsonObject data)
    {
        return new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["data"] = data } };
    }

    private static JsonObject ContactInline(string name)
    {
        return Module("contactInline", new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = name, ["phones"] = new JsonArray("+420 777 123 456") }) });
    }

    private static JsonObject YouTrack()
    {
        return new JsonObject
        {
            ["type"] = "youtrackIssue",
            ["attrs"] = new JsonObject { ["id"] = "2-12", ["key"] = "ABC-12", ["title"] = "Opravit přihlášení", ["url"] = "https://youtrack.example.com/issue/ABC-12", ["state"] = "Open" },
        };
    }

    private static JsonObject SecretInline()
    {
        return new JsonObject { ["type"] = "secretInlineReference", ["attrs"] = new JsonObject { ["secretId"] = SecretId, ["field"] = "password" } };
    }

    private static JsonObject SecretBlock()
    {
        return new JsonObject
        {
            ["type"] = "secretReference",
            ["attrs"] = new JsonObject { ["secretId"] = SecretId, ["fields"] = new JsonArray("username", "password"), ["showDetails"] = true },
        };
    }
}
