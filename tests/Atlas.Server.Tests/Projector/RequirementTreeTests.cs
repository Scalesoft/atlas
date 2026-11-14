using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Content;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>Finding, changing, inserting, moving and removing requirement blocks in page content (spec §2.1, §4), and the copy reset (§1.5).</summary>
public sealed class RequirementTreeTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    [Fact]
    public void An_update_changes_the_given_attributes_and_the_title_and_done_without_a_date_is_dated_today()
    {
        var rich = Rich(Block("a", "portal.ssl", "SSL"), Block("b", "portal.orders", "Objednávky"));

        RequirementTree.Update(rich, "a", new RequirementChange(Title: "SSL certifikát", State: "done", Party: "IT KBN", Requires: ["portal.orders"]), Today);

        var attrs = RequirementTree.Find(rich, "a")["attrs"]!;
        Assert.Equal(("done", "2026-10-27", "IT KBN", "portal.orders", "internal"),
            (attrs["state"]!.GetValue<string>(), attrs["date"]!.GetValue<string>(), attrs["party"]!.GetValue<string>(),
                attrs["requires"]!.GetValue<string>(), attrs["direction"]!.GetValue<string>()));
        Assert.Equal("SSL certifikát", RequirementTree.Title(RequirementTree.Find(rich, "a")));
        RequirementTree.Update(rich, "a", new RequirementChange(State: "planned"), Today);
        RequirementTree.Update(rich, "a", new RequirementChange(State: "done", Date: "2026-09-10"), Today);
        Assert.Equal("2026-09-10", RequirementTree.Find(rich, "a")["attrs"]!["date"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_prerequisites_name_the_keys_the_page_lacks_and_the_unfinished_blocks_that_need_them()
    {
        var rich = Rich(Block("a", "portal.ssl", "SSL"), Block("b", "portal.orders", "Objednávky"), Block("c", "portal.dns", "DNS"));
        RequirementTree.Update(rich, "a", new RequirementChange(Requires: ["portal.orders", "server", "other@00000000-0000-4000-8000-000000000001"]), Today);
        RequirementTree.Update(rich, "b", new RequirementChange(Requires: ["server"]), Today);
        RequirementTree.Update(rich, "c", new RequirementChange(State: "done", Requires: ["certs"]), Today);

        var warning = Assert.Single(RequirementTree.MissingPrerequisites(rich));
        Assert.Contains("„server“", warning);
        Assert.Contains("„SSL“, „Objednávky“", warning);
        Assert.Empty(RequirementTree.MissingPrerequisites(rich, new HashSet<string> { "c" }));
        Assert.Single(RequirementTree.MissingPrerequisites(rich, new HashSet<string> { "b" }));
        // An update that sets no requires looks at nothing, even on a page that misses a prerequisite.
        Assert.Empty(RequirementTree.MissingPrerequisites(rich, new HashSet<string>()));
    }

    [Theory]
    [InlineData("state", "invalid_requirement")]
    [InlineData("direction", "invalid_requirement")]
    [InlineData("date", "invalid_requirement")]
    [InlineData("self", "invalid_requirement")]
    [InlineData("key", "requirement_key_taken")]
    public void An_update_refuses_values_outside_the_node_contract(string field, string code)
    {
        var rich = Rich(Block("a", "portal.ssl", "SSL"), Block("b", "portal.orders", "Objednávky"));
        var change = field switch
        {
            "state" => new RequirementChange(State: "foo"),
            "direction" => new RequirementChange(Direction: "sideways"),
            "date" => new RequirementChange(Date: "2026-13-01"),
            "self" => new RequirementChange(Requires: ["portal.ssl"]),
            _ => new RequirementChange(Key: "portal.orders")
        };

        var refused = Assert.Throws<ApiException>(() => RequirementTree.Update(rich, "a", change, Today));

        Assert.Equal((400, code), (refused.Status, refused.Code));
    }

    [Fact]
    public void A_missing_block_is_404()
    {
        var refused = Assert.Throws<ApiException>(() => RequirementTree.Find(Rich(Paragraph("x")), "missing"));

        Assert.Equal((404, "requirement_not_found"), (refused.Status, refused.Code));
    }

    [Fact]
    public void Inserted_blocks_get_new_ids_reset_state_and_unique_keys_after_a_block_into_a_block_at_a_section_or_at_the_end()
    {
        var rich = Rich(Heading("Vstupy", "vstupy"), Block("a", "portal.ssl", "SSL"), Heading("Práce", "prace"), Paragraph("x"));
        var catalog = Block("c", "portal.ssl", "SSL z katalogu", state: "done", date: "2026-01-01", party: "IT", where: "Keepass");

        var afterBlock = Assert.Single(RequirementTree.Insert(rich, [catalog], new RequirementPosition(AfterBlockId: "a")));
        var intoBlock = Assert.Single(RequirementTree.Insert(rich, [Block("d", "portal.dns", "DNS")], new RequirementPosition(IntoBlockId: "a")));
        var atSection = Assert.Single(RequirementTree.Insert(rich, [Block("e", "portal.vpn", "VPN")], new RequirementPosition(HeadingId: "vstupy")));
        var atEnd = Assert.Single(RequirementTree.Insert(rich, [Block("f", "portal.end", "Konec")], new RequirementPosition()));

        var ids = new[] { afterBlock, intoBlock, atSection, atEnd }.Select(Id).ToArray();
        Assert.All(ids, id => Assert.True(Guid.TryParse(id, out _)));
        Assert.Equal($"heading a[{ids[1]}] {ids[0]} {ids[2]} heading paragraph {ids[3]}", Outline(rich));
        var attrs = afterBlock["attrs"]!;
        Assert.Equal(("portal.ssl-2", "planned", "", "", ""), (attrs["key"]!.GetValue<string>(), attrs["state"]!.GetValue<string>(),
            attrs["date"]!.GetValue<string>(), attrs["party"]!.GetValue<string>(), attrs["where"]!.GetValue<string>()));
        Assert.Equal("c", catalog["attrs"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void The_first_block_of_a_page_brings_the_requirement_plugin_into_the_document_once()
    {
        // A page without requirements takes them as requirement headings, which need the page they link from.
        var rich = Rich(Paragraph("x"));
        var page = Guid.NewGuid();

        RequirementTree.Insert(rich, [Block("c", "a", "A")], new RequirementPosition(), page);
        RequirementTree.Insert(rich, [Block("c", "b", "B")], new RequirementPosition(), page);

        Assert.Equal(["heading-anchors", "atlas-requirements"], rich["plugins"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal(2, rich["document"]!["content"]!.AsArray().Count(RequirementHeadings.IsRequirement));
        Rules.ValidateDocument(rich.ToJsonString());
    }

    [Fact]
    public void A_page_still_in_the_block_shape_brings_the_requirement_plugin_once_too()
    {
        // A page that still holds a requirement block (not converted yet) keeps the block shape.
        var rich = Rich(Block("a", "a", "A"), Paragraph("x"));
        rich["plugins"] = new JsonArray(new JsonObject { ["id"] = "heading-anchors", ["version"] = 1 });

        RequirementTree.Insert(rich, [Block("c", "b", "B")], new RequirementPosition());
        RequirementTree.Insert(rich, [Block("c", "c", "C")], new RequirementPosition());

        Assert.Equal(["heading-anchors", "atlas-requirements"], rich["plugins"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal(3, rich["document"]!["content"]!.AsArray().Count(x => RichDocument.TypeOf(x) == "requirement"));
    }

    [Fact]
    public void A_block_moves_after_another_or_into_one_and_never_into_itself()
    {
        var rich = Rich(Block("a", "a", "A", content: [Block("a1", "a1", "A1")]), Block("b", "b", "B"), Paragraph("x"));

        RequirementTree.Move(rich, "a1", new RequirementPosition(AfterBlockId: "b"));
        Assert.Equal("a b a1 paragraph", Outline(rich));
        RequirementTree.Move(rich, "a", new RequirementPosition(IntoBlockId: "b"));
        Assert.Equal("b[a] a1 paragraph", Outline(rich));
        var refused = Assert.Throws<ApiException>(() => RequirementTree.Move(rich, "b", new RequirementPosition(IntoBlockId: "a")));
        Assert.Equal((400, "invalid_position"), (refused.Status, refused.Code));
    }

    [Fact]
    public void Removing_the_last_block_leaves_a_valid_document()
    {
        var rich = Rich(Block("a", "a", "A", content: [Block("a1", "a1", "A1")]));

        RequirementTree.Remove(rich, "a1");
        Assert.Equal("a", Outline(rich));
        RequirementTree.Remove(rich, "a");
        Assert.Equal("paragraph", Outline(rich));
    }

    [Fact]
    public void A_copy_resets_state_date_party_and_where_with_new_ids_and_keeps_key_direction_requires_and_content()
    {
        var rich = Rich(Heading("Vstupy", "vstupy"),
            Block("a", "portal.ssl", "SSL", state: "done", direction: "inbound", requires: "portal.dns", date: "2026-09-10", party: "IT KBN",
                where: "Keepass", content: [Paragraph("Poznámka"), Block("a1", "portal.dns", "DNS", state: "blocked", party: "x")]),
            Summary("s"));

        RequirementTree.ResetForCopy(rich);

        var blocks = RequirementTree.Blocks(rich).ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.DoesNotContain(blocks, block => Id(block) is "a" or "a1");
        var attrs = blocks[0]["attrs"]!;
        Assert.Equal(("portal.ssl", "inbound", "portal.dns", "planned", "", "", ""), (attrs["key"]!.GetValue<string>(),
            attrs["direction"]!.GetValue<string>(), attrs["requires"]!.GetValue<string>(), attrs["state"]!.GetValue<string>(),
            attrs["date"]!.GetValue<string>(), attrs["party"]!.GetValue<string>(), attrs["where"]!.GetValue<string>()));
        Assert.Equal(("planned", ""), (blocks[1]["attrs"]!["state"]!.GetValue<string>(), blocks[1]["attrs"]!["party"]!.GetValue<string>()));
        Assert.Equal("Poznámka", blocks[0]["content"]![1]!["content"]![0]!["text"]!.GetValue<string>());
    }

    // Plan 3.5 C: a deployment made from another one („podle Dubovic“) takes the requirements, not that job's records.
    [Fact]
    public void A_copy_drops_the_history_lines_the_blocked_reason_and_the_custom_state_and_keeps_the_requirement_text()
    {
        static JsonObject List(params string[] lines) => new()
        {
            ["type"] = "bulletList",
            ["content"] = new JsonArray(lines.Select(line => (JsonNode)new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(Paragraph(line)) }).ToArray())
        };
        var blocked = Block("a", "portal.sms", "SMS brána", state: "blocked", content: [Paragraph("Operátor nedodal smlouvu"), Paragraph("Poskytovatel ze seznamu."),
            List("klient a typ VPN", "14. 10. 2026 09:30 — Blokováno: Operátor nedodal smlouvu (čekali jsme na IT KBN)", "1. 10. 2026 — Vyzváno: IT KBN")]);
        blocked["attrs"]!["stateLabel"] = "ověřit";
        // A timed list (rich revision 5), a fresh stamp without text too, is the job's records as a bullet list of them was.
        var timed = List("2. 9. 2026 — Dodáno", "3. 11. 2026 17:19 —");
        timed["type"] = "timedList";
        foreach (var item in timed["content"]!.AsArray())
        {
            item!["type"] = "timedItem";
        }
        var rich = Rich(blocked, Block("b", "portal.ssl", "SSL", content: [timed]));

        RequirementTree.ResetForCopy(rich);

        var blocks = RequirementTree.Blocks(rich).ToArray();
        static string Text(JsonNode? node) => node?["text"]?.GetValue<string>() ?? string.Concat((node?["content"]?.AsArray() ?? []).Select(Text));
        var notes = blocks[0]["content"]!.AsArray();
        Assert.Equal(["SMS brána", "Poskytovatel ze seznamu.", "klient a typ VPN"], notes.Select(Text));
        Assert.Null(blocks[0]["attrs"]!["stateLabel"]);
        Assert.Single(blocks[1]["content"]!.AsArray());
    }

    [Fact]
    public void Catalog_definitions_are_the_top_level_blocks_with_a_key_under_their_section_heading()
    {
        var pageId = Guid.NewGuid();
        var title = Paragraph("SSL ");
        title["content"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "text", ["text"] = "certifikát", ["marks"] = new JsonArray(new JsonObject { ["type"] = "bold" })
        });
        var ssl = Block("a", "portal.ssl", "", direction: "inbound", requires: "portal.dns, portal.vpn", content: [Block("a1", "nested", "Nested")]);
        ssl["content"]![0] = title;
        var rich = Rich(Block("x", "", "Bez klíče"), Block("y", "loose", "Před nadpisem"), Heading("Portál", "portal"), ssl);

        var definitions = RequirementTree.Definitions(pageId, rich);

        Assert.Equal(["loose", "portal.ssl"], definitions.Select(x => x.Key));
        var definition = definitions[1];
        Assert.Equal(("SSL certifikát", "SSL **certifikát**", "inbound", "Portál", pageId), (definition.Title, definition.TitleMarkdown,
            definition.Direction, definition.Bundle, definition.PageId));
        Assert.Equal(["portal.dns", "portal.vpn"], definition.Requires);
        Assert.Equal("", definitions[0].Bundle);
        Assert.True(JsonNode.DeepEquals(ssl, definition.ContentJson));
        Assert.NotSame(ssl, definition.ContentJson);
    }

    /// <summary>Plan 3.1 §2.3 (P1): the module of a definition is the heading above it, by its stable headingId, else the slug of its text.</summary>
    [Fact]
    public void A_definition_names_its_module_by_the_heading_id_or_the_slug_of_the_heading_text()
    {
        var plain = new JsonObject
        {
            ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = 2 }, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Účetní modul" })
        };
        var rich = Rich(Block("a", "intro", "Úvod"), Heading("Portál zákazníků", "portal"), Block("b", "portal.ssl", "SSL"), plain, Block("c", "acc.export", "Export"));

        var definitions = RequirementTree.Definitions(Guid.NewGuid(), rich);

        Assert.Equal([("intro", ""), ("portal.ssl", "portal"), ("acc.export", "ucetni-modul")], definitions.Select(x => (x.Key, x.ModuleKey)));
    }

    /// <summary>Plan 3.4 §5: a module's category is the heading one level above its own, „Jádro“ without one.</summary>
    [Fact]
    public void A_definition_names_its_category_by_the_heading_above_its_module()
    {
        var rich = Rich(Block("a", "intro", "Úvod"), Heading("Autentizace", "auth"), Heading("NIA", "nia", 3), Block("b", "nia.cert", "Certifikát"),
            Heading("eRecept", "erecept", 3), Block("c", "erecept.sukl", "SÚKL"), Heading("Core", "core"), Block("d", "core.vpn", "VPN"));

        var definitions = RequirementTree.Definitions(Guid.NewGuid(), rich);

        Assert.Equal([("intro", "Jádro"), ("nia.cert", "Autentizace"), ("erecept.sukl", "Autentizace"), ("core.vpn", "Jádro")],
            definitions.Select(x => (x.Key, x.Category)));
    }

    /// <summary>Plan 3.5 C: the product is the heading above the category (# Portál › ## Autentizace › ### NIA), "" without one.</summary>
    [Fact]
    public void A_definition_names_its_product_by_the_heading_above_its_category()
    {
        var rich = Rich(Heading("Portál pacienta", "portal", 1), Heading("Rezervace", "booking"), Heading("Webové objednávání", "web", 3), Block("a", "portal.web", "Web"),
            Heading("MPI", "mpi", 1), Block("b", "mpi.core", "Core"), Heading("Integrace", "int"), Block("c", "mpi.uzis", "ÚZIS"));

        var definitions = RequirementTree.Definitions(Guid.NewGuid(), rich);

        Assert.Equal([("portal.web", "Rezervace", "Portál pacienta"), ("mpi.core", "Jádro", ""), ("mpi.uzis", "MPI", "")],
            definitions.Select(x => (x.Key, x.Category, x.Product)));
    }

    /// <summary>O7: a definition's summary is the text of the paragraphs under its module heading before the first requirement block.</summary>
    [Fact]
    public void A_definition_carries_the_paragraphs_under_its_module_heading_as_its_summary()
    {
        var rich = Rich(Paragraph("Před nadpisem"), Heading("Instalace", "inst"), Paragraph("Základní instalace portálu."), Paragraph(""), Paragraph("Dvě veřejné URL."),
            Block("a", "portal.install", "Instalace"), Paragraph("Poznámka po bloku"), Heading("Bez popisu", "none"), Block("b", "portal.none", "Nic"));

        var definitions = RequirementTree.Definitions(Guid.NewGuid(), rich);

        Assert.Equal(["Základní instalace portálu. Dvě veřejné URL.", ""], definitions.Select(x => x.Summary));
    }

    /// <summary>O5: a block goes to the end of the section its path names; missing headings are created one level deeper; the rest goes last.</summary>
    [Fact]
    public void Blocks_are_placed_by_their_path_with_missing_headings_created_and_unmatched_ones_last()
    {
        var rich = Rich(Heading("Portál pacienta", "p", 1), Heading("Rezervace", "r"), Block("old", "portal.old", "Old"), Heading("Autentizace", "a"), Heading("MPI", "m", 1),
            Block("mpi", "mpi.x", "X"));
        var blocks = new[] { Block("n1", "portal.n1", "N1"), Block("n2", "portal.n2", "N2"), Block("n3", "portal.n3", "N3"), Block("n4", "portal.n4", "N4") };

        var copies = RequirementTree.InsertByPath(rich, blocks, [["portal pacienta", "REZERVACE", "Web"], ["Portál pacienta", "Rezervace", "Web"], ["Portál pacienta", "Autentizace"], ["Nikde"]]);

        Assert.Equal(["n1", "n2", "n3", "n4"], copies.Select(RequirementTree.Key).Select(key => key.Replace("portal.", "")));
        var page = (JsonArray)rich["document"]!["content"]!;
        Assert.Equal(["Portál pacienta", "Rezervace", "portal.old", "Web", "portal.n1", "portal.n2", "Autentizace", "portal.n3", "MPI", "mpi.x", "portal.n4"],
            page.Select(node => node!["type"]!.ToString() == "heading" ? node["content"]![0]!["text"]!.ToString() : RequirementTree.Key((JsonObject)node!)));
        Assert.Equal(3, (int)page[3]!["attrs"]!["level"]!);
        Assert.False(RequirementTree.OnPage(rich, new[] { "Nikde" }));
    }

    /// <summary>Plan 3.4 §4, §5: the block's own word for its state and its service are text attributes; a new state clears the word.</summary>
    [Fact]
    public void An_update_writes_the_own_state_word_and_the_service_and_a_new_state_without_a_word_clears_it()
    {
        var rich = Rich(Block("a", "nia", "NIA"));
        string Attr(string name) => RequirementAttributes.Text(RequirementTree.Blocks(rich).Single()["attrs"], name);

        RequirementTree.Update(rich, "a", new RequirementChange(State: "in_progress", StateLabel: "ověřit", System: "NIA"), Today);
        Assert.Equal(("in_progress", "ověřit", "NIA"), (Attr("state"), Attr("stateLabel"), Attr("system")));
        RequirementTree.Update(rich, "a", new RequirementChange(State: "in_progress", Party: "IT"), Today);
        Assert.Equal("ověřit", Attr("stateLabel"));
        RequirementTree.Update(rich, "a", new RequirementChange(State: "done"), Today);
        Assert.Equal(("done", "", "NIA"), (Attr("state"), Attr("stateLabel"), Attr("system")));
        Assert.Equal("invalid_requirement", Assert.Throws<ApiException>(() =>
            RequirementTree.Update(rich, "a", new RequirementChange(StateLabel: new string('x', 201)), Today)).Code);

        RequirementTree.Update(rich, "a", new RequirementChange(StateLabel: "čeká na podpis"), Today);
        RequirementTree.ResetForCopy(rich);
        Assert.Equal(("planned", "", "NIA"), (Attr("state"), Attr("stateLabel"), Attr("system")));
    }

    [Fact]
    public void A_module_heading_goes_one_level_below_the_heading_above_the_place()
    {
        var rich = Rich(Block("a", "intro", "Úvod"), Heading("Portál", "portal"), Block("b", "portal.ssl", "SSL", content: [Block("b1", "nested", "Vnořený")]),
            Heading("NIA", "nia", 3), Heading("Detail", "detail", 6));

        Assert.Equal(2, RequirementTree.HeadingLevel(rich, new RequirementPosition(AfterBlockId: "a")));
        Assert.Equal(3, RequirementTree.HeadingLevel(rich, new RequirementPosition(HeadingId: "portal")));
        Assert.Equal(3, RequirementTree.HeadingLevel(rich, new RequirementPosition(AfterBlockId: "b")));
        Assert.Equal(2, RequirementTree.HeadingLevel(rich, new RequirementPosition(IntoBlockId: "b")));
        Assert.Equal(6, RequirementTree.HeadingLevel(rich, new RequirementPosition()));
    }

    [Fact]
    public void Moving_the_only_block_out_of_a_container_leaves_the_container_valid()
    {
        var quote = new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(Block(B, "b", "B")) };
        var rich = Rich(Block(A, "a", "A"), quote);

        RequirementTree.Move(rich, B, new RequirementPosition(AfterBlockId: A));

        Assert.Equal($"{A} {B} blockquote", Outline(rich));
        Rules.ValidateDocument(rich.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inserted_content_brings_the_plugins_of_all_its_nodes(bool blockShape)
    {
        // A page without requirements takes a requirement heading (it needs the page); one still holding a block keeps the block shape.
        var rich = blockShape ? Rich(Block(A, "stary", "Starý"), Paragraph("x")) : Rich(Paragraph("x"));
        rich["plugins"] = new JsonArray();
        var title = Paragraph("SSL ");
        title["content"]!.AsArray().Add(new JsonObject { ["type"] = "emoji", ["attrs"] = new JsonObject { ["name"] = "rocket" } });
        var block = Block(C, "portal.ssl", "", content: [Heading("Postup", "postup", 3)]);
        block["content"]![0] = title;

        RequirementTree.Insert(rich, [block], new RequirementPosition(), blockShape ? null : Guid.NewGuid());

        Assert.Equal(blockShape ? 2 : 0, rich["document"]!["content"]!.AsArray().Count(x => RichDocument.TypeOf(x) == "requirement"));

        Assert.Equal(["atlas-requirements", "emoji", "heading-anchors"], rich["plugins"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Rules.ValidateDocument(rich.ToJsonString());
    }

    [Fact]
    public void A_stored_block_without_default_attributes_reads_as_the_schema_defaults()
    {
        var partial = new JsonObject { ["type"] = "requirement", ["attrs"] = new JsonObject { ["id"] = A, ["key"] = "k" }, ["content"] = new JsonArray(Paragraph("A")) };
        var rich = Rich(partial);

        RequirementTree.Update(rich, A, new RequirementChange(State: "done"), Today);

        Assert.Equal("done", partial["attrs"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_server_write_gives_id_less_and_repeated_blocks_new_ids_and_keyless_blocks_the_slug_of_their_title()
    {
        var bare = new JsonObject { ["type"] = "requirement", ["content"] = new JsonArray(Paragraph("SSL certifikát")) };
        var nullId = Block(A, "", "SSL certifikát");
        nullId["attrs"]!["id"] = null;
        var rich = Rich(Block(A, "a", "A"), bare, nullId, Block(A, "repeated", "Opakovaný"), Block(B, "", ""));

        RequirementTree.Normalize(rich);

        var blocks = RequirementTree.Blocks(rich).ToArray();
        Assert.Equal(5, blocks.Select(Id).Distinct().Count());
        Assert.Equal(A, Id(blocks[0]));
        Assert.All(blocks, block => Assert.True(Guid.TryParse(Id(block), out _)));
        Assert.Equal(["a", "ssl-certifikat", "ssl-certifikat-2", "repeated", ""], blocks.Select(RequirementTree.Key));
    }

    [Fact]
    public void A_renamed_key_moves_the_dependencies_of_the_page_with_it()
    {
        var rich = Rich(Block(A, "portal.ssl", "SSL"), Block(B, "orders", "Objednávky", requires: "portal.ssl,elsewhere"), Block(C, "tested", "Test", requires: "portal.ssl"));

        var warnings = RequirementTree.Update(rich, A, new RequirementChange(Key: "portal.tls"), Today);

        Assert.Equal(["portal.tls,elsewhere", "portal.tls"], new[] { B, C }.Select(id => RequirementTree.Find(rich, id)["attrs"]!["requires"]!.GetValue<string>()));
        Assert.Equal(["Závislosti požadavků „Objednávky“, „Test“ teď vedou na nový klíč „portal.tls“."], warnings);
    }

    /// <summary>Review S2-1: new notes bring the nested blocks back as new nodes, and their requires follow the renamed key too.</summary>
    [Fact]
    public void A_renamed_key_with_new_notes_moves_the_dependencies_of_the_blocks_nested_in_those_notes()
    {
        var rich = Rich(Block(A, "portal.ssl", "SSL", content: [Paragraph("Poznámka"), Block(B, "tested", "Test", requires: "portal.ssl")]));

        var warnings = RequirementTree.Update(rich, A, new RequirementChange(Key: "portal.tls", NotesMarkdown: "Nová poznámka\n\n⟦c:1⟧"), Today);

        Assert.Equal("portal.tls", RequirementTree.Find(rich, B)["attrs"]!["requires"]!.GetValue<string>());
        Assert.Equal(["Závislosti požadavků „Test“ teď vedou na nový klíč „portal.tls“."], warnings);
    }

    [Fact]
    public void A_cleared_key_takes_the_slug_of_the_title_and_without_one_the_dependencies_on_it_go_with_a_warning()
    {
        var rich = Rich(Block(A, "portal.ssl", "SSL certifikát"), Block(B, "x", ""), Block(C, "tested", "Test", requires: "portal.ssl,x"));

        RequirementTree.Update(rich, A, new RequirementChange(Key: ""), Today);
        var warnings = RequirementTree.Update(rich, B, new RequirementChange(Key: ""), Today);

        Assert.Equal("ssl-certifikat", RequirementTree.Key(RequirementTree.Find(rich, A)));
        Assert.Equal("", RequirementTree.Key(RequirementTree.Find(rich, B)));
        Assert.Equal("ssl-certifikat", RequirementTree.Find(rich, C)["attrs"]!["requires"]!.GetValue<string>());
        Assert.Equal(["Požadavky „Test“ už nezávisí na „x“: požadavek bez názvu nemá klíč."], warnings);
    }

    [Fact]
    public void The_title_reads_a_line_break_as_a_space_as_the_projection_does()
    {
        var title = Paragraph(" SSL");
        title["content"]!.AsArray().Add(new JsonObject { ["type"] = "hardBreak" });
        title["content"]!.AsArray().Add(new JsonObject { ["type"] = "text", ["text"] = "certifikát " });
        var block = Block(A, "a", "");
        block["content"]![0] = title;

        Assert.Equal("SSL certifikát", RequirementTree.Title(block));
    }

    /// <summary>A key typed into a title becomes the editor's YouTrack node (M4c); the title keeps it as its key, as the web reads it.</summary>
    [Fact]
    public void The_title_reads_a_youtrack_issue_as_its_key()
    {
        var title = Paragraph("Opravit ");
        title["content"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "youtrackIssue",
            ["attrs"] = new JsonObject { ["id"] = "2-1", ["key"] = "PX2-1235", ["title"] = "Pád", ["url"] = "https://yt.example/issue/PX2-1235" },
        });
        title["content"]!.AsArray().Add(new JsonObject { ["type"] = "text", ["text"] = " dnes" });
        var block = Block(A, "a", "");
        block["content"]![0] = title;

        Assert.Equal("Opravit PX2-1235 dnes", RequirementTree.Title(block));
    }

    public static TheoryData<string, string, bool> HistoryStamps()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var entry in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "history-stamps.json")))!.AsArray())
        {
            data.Add((string)entry!["name"]!, (string)entry["text"]!, (bool)entry["history"]!);
        }
        return data;
    }

    /// <summary>The table requirementHistory.test.ts reads too: a copy strips exactly the lines the editor takes for history.</summary>
    [Theory]
    [MemberData(nameof(HistoryStamps))]
    public void Tells_a_history_line_as_the_editor_does(string name, string text, bool history)
    {
        Assert.True(RequirementTree.IsHistoryLine(text) == history, name);
    }

    private const string A = "00000000-0000-4000-8000-00000000000a", B = "00000000-0000-4000-8000-00000000000b", C = "00000000-0000-4000-8000-00000000000c";

    private static string Id(JsonNode block) => block["attrs"]!["id"]!.GetValue<string>();
}
