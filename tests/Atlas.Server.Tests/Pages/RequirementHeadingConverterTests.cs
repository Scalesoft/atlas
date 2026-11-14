using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>The 3.6 conversion of requirement blocks to requirement headings (plan 2026-11-04 §5, model §10b mapping table), one field
/// at a time over invented pages: the heading and its level, ids and aliases (dotted, duplicate, outside the charset, taken), the
/// dependency line (same page, other page, unresolved), the party on the heading and the deadline line (§6), „Kde uloženo“, the nested
/// block, an empty title, loose content, idempotence, the gate that finds a loss or a gain, and the move of a version-3 page.</summary>
public sealed class RequirementHeadingConverterTests
{
    private static readonly Guid Page = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private const string A = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601", B = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e602", C = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e603",
        Contact = "7a1d2c3b-4a5e-4f60-8a71-92b3c4d5e6ff";

    [Fact]
    public void Content_that_is_missing_or_unreadable_is_not_an_old_shape()
    {
        // A create request without contentJson reaches the conversion as null; the validation refuses it (400), the conversion must not throw.
        Assert.False(RequirementHeadingConverter.OldShape(null));
        Assert.False(RequirementHeadingConverter.OldShape(""));
        Assert.False(RequirementHeadingConverter.OldShape("""{"requirement":"""));
    }

    internal static string Envelope(params string[] blocks) => $$$"""
        {"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":2}],"document":{"type":"doc","content":[{{{string.Join(",", blocks)}}}]}}
        """;

    internal static string Heading(int level, string id, string text) => $$"""{"type":"heading","attrs":{"level":{{level}},"headingId":"{{id}}"},"content":[{"type":"text","text":"{{text}}"}]}""";

    internal static string Paragraph(string text) => $$"""{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}""";

    private static string List(params string[] items)
        => """{"type":"bulletList","content":[""" + string.Join(",", items.Select(x => """{"type":"listItem","content":[""" + Paragraph(x) + "]}")) + "]}";

    internal static string Block(string id, string key, string title, string state = "planned", string direction = "internal", string party = "", string contact = "",
        string date = "", string where = "", string requires = "", string stateLabel = "", string system = "", params string[] body)
    {
        var attrs = new JsonObject
        {
            ["id"] = id, ["key"] = key, ["direction"] = direction, ["state"] = state, ["stateLabel"] = stateLabel, ["date"] = date, ["party"] = party,
            ["partyContactId"] = contact, ["where"] = where, ["system"] = system, ["requires"] = requires,
        };
        var titleParagraph = title.Length == 0 ? """{"type":"paragraph"}""" : Paragraph(title);
        return $$"""{"type":"requirement","attrs":{{attrs.ToJsonString()}},"content":[{{string.Join(",", body.Prepend(titleParagraph))}}]}""";
    }

    private static JsonNode Document(string json) => JsonNode.Parse(json)!["document"]!;

    private static RequirementConversion Convert(string json, RequirementKeyMap? map = null)
        => RequirementHeadingConverter.Convert(json, Page, map ?? RequirementKeyMap.Build(RequirementKeyMap.Rows(Page, true, Document(json))));

    private static JsonArray Content(RequirementConversion conversion) => Document(conversion.Json)["content"]!.AsArray();

    private static string Compact(JsonNode? node) => node!.ToJsonString(RichDocument.JsonWrite);

    [Fact]
    public void Every_field_of_an_inbound_block_goes_to_its_heading_and_section()
    {
        var json = Envelope(Heading(2, "h-portal", "Portál"),
            Block(A, "portal.ssl", "SSL certifikát", state: "waiting_for_input", direction: "inbound", party: "IT nemocnice", contact: Contact, date: "2026-11-15",
                where: "Atlas › Servery; https://portal.example/admin, heslo v trezoru", requires: "portal.dns,chybi.klic", stateLabel: "ověřit", system: "NIA",
                body: [Paragraph("Poznámka k certifikátu"), List("1. 11. 2026 vyžádáno", "3. 11. 2026 urgováno")]),
            Block(B, "portal.dns", "DNS záznam"));

        var conversion = Convert(json);

        Assert.True(conversion.Changed);
        var content = Content(conversion);
        Assert.Equal("""{"type":"heading","attrs":{"level":3,"headingId":"portal.ssl","state":"waiting_for_input","stateLabel":"ověřit","direction":"inbound","system":"NIA","from":"","aliases":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601,portal.ssl","party":"IT nemocnice","partyContactId":"7a1d2c3b-4a5e-4f60-8a71-92b3c4d5e6ff"},"content":[{"type":"text","text":"SSL certifikát"}]}""",
            Compact(content[1]));
        Assert.Equal($$$"""{"type":"paragraph","attrs":{"dependencies":true},"content":[{"type":"pageReference","attrs":{"id":"{{{Page}}}","title":"DNS záznam","url":"#/pages/{{{Page}}}?heading=portal.dns"}}]}""",
            Compact(content[2]));
        Assert.Equal(Paragraph("do 15. 11. 2026"), Compact(content[3]));
        Assert.Equal(Paragraph("Chybějící prerekvizita: chybi.klic"), Compact(content[4]));
        Assert.Equal("""{"type":"paragraph","content":[{"type":"text","text":"Kde uloženo: Atlas › Servery; "},{"type":"text","text":"https://portal.example/admin","marks":[{"type":"link","attrs":{"href":"https://portal.example/admin","target":"_blank","rel":"noopener noreferrer"}}]},{"type":"text","text":", heslo v trezoru"}]}""",
            Compact(content[5]));
        Assert.Equal(Paragraph("Poznámka k certifikátu"), Compact(content[6]));
        Assert.Equal(List("1. 11. 2026 vyžádáno", "3. 11. 2026 urgováno"), Compact(content[7]));
        Assert.Equal("portal.dns", (string?)content[8]!["attrs"]!["headingId"]);
        Assert.Equal(9, content.Count);
        Assert.Equal(4, (int)JsonNode.Parse(conversion.Json)!["plugins"]![0]!["version"]!);

        var report = conversion.Report;
        Assert.True(report.Passes);
        Assert.Equal(report.BeforeCounts.Requirements, report.AfterCounts.Requirements);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report.BeforeCounts), System.Text.Json.JsonSerializer.Serialize(report.AfterCounts));
        Assert.Equal((2, 1, 1, 1, 1, 1, 1, 2), (report.AfterCounts.Dependencies, report.AfterCounts.Unresolved, report.AfterCounts.Parties,
            report.AfterCounts.PartiesWithContact, report.AfterCounts.Deadlines, report.AfterCounts.Where, report.AfterCounts.Notes, report.AfterCounts.HistoryLines));
        // The unresolved key is the one loss, accepted as a reported case.
        var loss = Assert.Single(report.Losses);
        Assert.Equal(("dependency", "?chybi.klic", "unresolved key"), (loss.Field, loss.Before, loss.Accepted));
        Assert.Contains(report.Cases, x => x.Kind == RequirementCaseKind.UnresolvedKey && x.BlockId == A && x.Detail == "same page");
        Assert.Empty(report.Gains);
    }

    [Fact]
    public void The_party_goes_to_the_heading_and_the_facts_line_is_the_deadline_alone_left_out_without_one()
    {
        var json = Envelope(
            Block(A, "a", "Naše práce", party: "Jan Novák"),
            Block(B, "b", "Předání", direction: "outbound", party: "Dodavatel s.r.o.", date: "2027-03-05"),
            Block(C, "c", "Vstup bez strany", direction: "inbound", date: "2027-03-05"),
            Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e604", "d", "Bez faktů", direction: "inbound"));

        var conversion = Convert(json);

        var content = Content(conversion);
        Assert.Equal(new[] { "heading", "heading", "paragraph", "heading", "paragraph", "heading" }, content.Select(RichDocument.TypeOf));
        Assert.Equal(new[] { "Jan Novák", "Dodavatel s.r.o.", null, null }, content.Where(RequirementHeadingReader.IsRequirementHeading).Select(x => (string?)x!["attrs"]!["party"]));
        Assert.Null(content[0]!["attrs"]!["partyContactId"]);
        Assert.Equal(Paragraph("do 5. 3. 2027"), Compact(content[2]));
        Assert.Equal(Paragraph("do 5. 3. 2027"), Compact(content[4]));
        Assert.True(conversion.Report.Passes);
        Assert.Empty(conversion.Report.Losses);
        Assert.Equal(new[] { "internal", "outbound", "inbound", "inbound" }, conversion.Report.After.Select(x => x.Direction));
        Assert.Equal(new[] { "Jan Novák", "Dodavatel s.r.o.", "", "" }, conversion.Report.After.Select(x => x.Party));
        Assert.Equal(new[] { "", "2027-03-05", "2027-03-05", "" }, conversion.Report.After.Select(x => x.Date));
    }

    [Fact]
    public void A_heading_is_one_level_under_the_nearest_plain_heading_at_most_h6()
    {
        var json = Envelope(Block(A, "a", "Bez nadpisu"), Heading(3, "h3", "Moduly"), Block(B, "b", "První"), Block(C, "c", "Druhý"),
            Heading(6, "h6", "Hluboko"), Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e604", "d", "Na dně"));

        var conversion = Convert(json);

        Assert.Equal(new[] { 1, 3, 4, 4, 6, 6 }, Content(conversion).Where(x => RichDocument.TypeOf(x) == "heading").Select(x => (int)x!["attrs"]!["level"]!));
        Assert.Equal("h7", Assert.Single(conversion.Report.Cases).Detail);
        Assert.True(conversion.Report.Passes);
    }

    [Fact]
    public void A_heading_id_is_the_key_when_valid_and_unique_else_the_old_id_and_both_stay_as_aliases()
    {
        var json = Envelope(Heading(2, "taken.key", "Obsazený klíč"),
            Block(A, "nemocnice.vpn.ipsec", "VPN"),
            Block(B, "dvakrat", "První se stejným klíčem"), Block(C, "dvakrat", "Druhý se stejným klíčem"),
            Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e604", "Špatný klíč", "Klíč mimo znaky"),
            Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e605", "taken.key", "Klíč nadpisu"),
            Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e606", "", "Bez klíče", requires: "dvakrat"));

        var conversion = Convert(json);

        var headings = Content(conversion).Where(RequirementHeadingReader.IsRequirementHeading).ToList();
        Assert.Equal(new[] { "nemocnice.vpn.ipsec", B, C, "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e604", "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e605", "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e606" },
            headings.Select(x => (string?)x!["attrs"]!["headingId"]));
        Assert.Equal($"{A},nemocnice.vpn.ipsec", (string?)headings[0]!["attrs"]!["aliases"]);
        Assert.Equal("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e606", (string?)headings[5]!["attrs"]!["aliases"]);
        // A repeated key resolves to its last block, as the web's Map did, and is reported.
        Assert.Equal($"{Page:D}#{C}", Assert.Single(conversion.Report.After[5].Dependencies));
        Assert.Contains(conversion.Report.Cases, x => x.Kind == RequirementCaseKind.DuplicateKey && x.BlockId == C && x.Detail == "2 blocks");
        Assert.Equal(new[] { "key not unique", "key not unique", "key outside charset", "taken by a heading", "no key" },
            conversion.Report.Cases.Where(x => x.Kind == RequirementCaseKind.IdFallback).Select(x => x.Detail));
        Assert.True(conversion.Report.Passes);
    }

    [Fact]
    public void Working_and_published_revisions_give_one_block_the_same_heading_id()
    {
        // The working revision added a block with the published block's key: across the page the key is not unique any more.
        var published = Envelope(Block(A, "portal.vpn", "VPN"), Block(C, "portal.dns", "DNS", requires: "portal.vpn"));
        var working = Envelope(Block(A, "portal.vpn", "VPN"), Block(B, "portal.vpn", "VPN záložní"), Block(C, "portal.dns", "DNS", requires: "portal.vpn"));
        var map = RequirementKeyMap.Build(RequirementKeyMap.Rows(Page, false, Document(published)).Concat(RequirementKeyMap.Rows(Page, true, Document(working))));

        var fromPublished = Convert(published, map);
        var fromWorking = Convert(working, map);

        Assert.Equal(A, fromPublished.Report.After[0].Id);
        Assert.Equal(A, fromWorking.Report.After[0].Id);
        Assert.Equal("portal.dns", fromPublished.Report.After[1].Id);
        Assert.Equal("portal.dns", fromWorking.Report.After[2].Id);
        // Each revision's dependency names its own block holding the key.
        Assert.Equal($"{Page:D}#{A}", Assert.Single(fromPublished.Report.After[1].Dependencies));
        Assert.Equal($"{Page:D}#{B}", Assert.Single(fromWorking.Report.After[2].Dependencies));
    }

    [Fact]
    public void A_dependency_on_another_page_links_its_heading_and_an_unknown_one_is_a_missing_line()
    {
        var other = Envelope(Block(B, "spolecne.ad", "Active Directory"));
        var json = Envelope(Block(A, "a", "Přihlášení", requires: $"spolecne.ad@{Other:D},neni@{Other:D}"));
        var map = RequirementKeyMap.Build(RequirementKeyMap.Rows(Page, true, Document(json)).Concat(RequirementKeyMap.Rows(Other, true, Document(other))));

        var conversion = Convert(json, map);

        var content = Content(conversion);
        Assert.Equal($$$"""{"type":"paragraph","attrs":{"dependencies":true},"content":[{"type":"pageReference","attrs":{"id":"{{{Other}}}","title":"Active Directory","url":"#/pages/{{{Other}}}?heading=spolecne.ad"}}]}""",
            Compact(content[1]));
        Assert.Equal(Paragraph($"Chybějící prerekvizita: neni@{Other:D}"), Compact(content[2]));
        Assert.Contains(conversion.Report.Cases, x => x.Kind == RequirementCaseKind.UnresolvedKey && x.Detail == "other page");
        Assert.Equal(new[] { $"{Other:D}#spolecne.ad", $"?neni@{Other:D}" }, conversion.Report.After[0].Dependencies);
        Assert.True(conversion.Report.Passes);
    }

    [Fact]
    public void A_nested_block_becomes_a_plain_heading_with_its_notes_first_a_former_line_and_its_children_under_it()
    {
        var json = Envelope(Heading(2, "h2", "Integrace"),
            Block(A, "lab", "Laboratoř", state: "done", party: "Oddělení laboratoře", date: "2026-10-01",
                body: [Paragraph("Před vnořeným"), Block(B, "lab.hl7", "HL7 zprávy", state: "in_progress"), Paragraph("Za vnořeným")]),
            Block(C, "rtg", "Rentgen"));

        var conversion = Convert(json);

        var content = Content(conversion);
        Assert.Equal("""{"type":"heading","attrs":{"level":3,"headingId":"lab","aliases":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601,lab"},"content":[{"type":"text","text":"Laboratoř"}]}""", Compact(content[1]));
        Assert.Equal(Paragraph("Dříve požadavek: hotovo 1. 10. 2026 · naše práce · kdo: Oddělení laboratoře · klíč: lab"), Compact(content[2]));
        Assert.Equal(Paragraph("Před vnořeným"), Compact(content[3]));
        Assert.Equal(Paragraph("Za vnořeným"), Compact(content[4]));
        Assert.Equal((4, "lab.hl7", "in_progress"), ((int)content[5]!["attrs"]!["level"]!, (string?)content[5]!["attrs"]!["headingId"], (string?)content[5]!["attrs"]!["state"]));
        // The next block keeps its level: it was the parent's sibling.
        Assert.Equal((3, "rtg"), ((int)content[6]!["attrs"]!["level"]!, (string?)content[6]!["attrs"]!["headingId"]));
        Assert.Equal(new[] { RequirementCaseKind.Nested, RequirementCaseKind.NotesMoved }, conversion.Report.Cases.Select(x => x.Kind));
        Assert.Equal(("requirement", "nested"), (Assert.Single(conversion.Report.Losses).Field, conversion.Report.Losses[0].Accepted));
        Assert.Equal((3, 2), (conversion.Report.BeforeCounts.Requirements, conversion.Report.AfterCounts.Requirements));
        Assert.True(conversion.Report.Passes);
    }

    [Fact]
    public void An_empty_title_is_an_empty_heading_and_a_reported_case()
    {
        var conversion = Convert(Envelope(Block(A, "a", "", body: [Paragraph("Jen poznámka")])));

        Assert.Equal("""{"type":"heading","attrs":{"level":1,"headingId":"a","state":"planned","stateLabel":"","direction":"internal","system":"","from":"","aliases":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601,a"}}""",
            Compact(Content(conversion)[0]));
        Assert.Equal(RequirementCaseKind.EmptyTitle, Assert.Single(conversion.Report.Cases).Kind);
        Assert.True(conversion.Report.Passes);
    }

    [Fact]
    public void Loose_content_after_a_block_joins_its_section_as_a_reported_gain()
    {
        var conversion = Convert(Envelope(Block(A, "a", "První"), Paragraph("Mezi bloky"), Heading(2, "h", "Další"), Paragraph("Pod nadpisem")));

        Assert.Equal(("paragraph", A), (Assert.Single(conversion.Report.Cases).Detail, conversion.Report.Cases[0].BlockId));
        Assert.Equal(("notes", "loose content"), (Assert.Single(conversion.Report.Gains).Field, conversion.Report.Gains[0].Accepted));
        Assert.True(conversion.Report.Passes);
    }

    [Fact]
    public void A_converted_document_comes_back_unchanged()
    {
        var first = Convert(Envelope(Heading(2, "h", "Moduly"), Block(A, "a.b", "První", party: "Jan", date: "2026-12-01", requires: "c", body: [List("řádek")]),
            Block(C, "c", "Druhý", where: "https://x.example")));

        var second = Convert(first.Json, RequirementKeyMap.Build([]));

        Assert.False(second.Changed);
        Assert.Same(first.Json, second.Json);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.Report.AfterCounts), System.Text.Json.JsonSerializer.Serialize(second.Report.BeforeCounts));
        Assert.True(second.Report.Passes);
        Assert.Empty(second.Report.Cases);
    }

    [Fact]
    public void A_block_whose_key_a_catalog_page_defines_takes_that_definition_as_from()
    {
        var catalog = Envelope(Block(B, "portal.vpn", "VPN"));
        var json = Envelope(Block(A, "portal.vpn", "VPN"), Block(C, "vlastni", "Vlastní"));
        var map = RequirementKeyMap.Build(RequirementKeyMap.Rows(Page, true, Document(json)).Concat(RequirementKeyMap.Rows(Other, false, Document(catalog))));

        var content = Content(RequirementHeadingConverter.Convert(json, Page, map, [Other]));

        Assert.Equal(new[] { $"{Other:D}#portal.vpn", "" }, content.Where(RequirementHeadingReader.IsRequirementHeading).Select(x => (string?)x!["attrs"]!["from"]));
        // The catalog page itself names no origin.
        Assert.Equal("", (string?)Content(RequirementHeadingConverter.Convert(catalog, Other, map, [Other]))[0]!["attrs"]!["from"]);
    }

    /// <summary>Review 2026-11-05: a map that did not read the space's catalog page would write `from` empty; the gate counts that as a lost
    /// `from` and refuses the page. So does any other attribute the heading would lose.</summary>
    [Fact]
    public void A_from_the_map_could_not_read_is_a_loss_the_gate_refuses()
    {
        var json = Envelope(Block(A, "portal.vpn", "VPN"));
        var unread = RequirementKeyMap.Build(RequirementKeyMap.Rows(Page, true, Document(json)), [Page]);

        var conversion = RequirementHeadingConverter.Convert(json, Page, unread, [Other]);

        Assert.False(conversion.Report.Passes);
        Assert.Equal(("from", $"?{Other:D}#portal.vpn", ""), Assert.Single(conversion.Report.Losses.Select(x => (x.Field, x.Before, x.After))));
        var document = Document(conversion.Json);
        document["content"]![0]!["attrs"]!["aliases"] = "";
        document["content"]![0]!["attrs"]!["system"] = "";
        var stripped = RequirementConversionReport.Compare(Page, Convert(Envelope(Block(A, "portal.vpn", "VPN", system: "NIA"))).Report.Before,
            RequirementHeadingReader.Read(Page, document), []);
        Assert.Equal(new[] { "aliases", "system" }, stripped.Losses.Select(x => x.Field).Order());
    }

    [Fact]
    public void A_converted_page_passes_the_editor_schema_and_reads_back_from_its_markdown()
    {
        var json = Envelope(Heading(2, "h-portal", "Portál"), Paragraph("Úvod"), """{"type":"paragraph"}""",
            Block(A, "portal.ssl", "SSL certifikát", state: "waiting_for_input", direction: "inbound", party: "IT nemocnice", contact: Contact, date: "2026-11-15",
                where: "Atlas › Servery; https://portal.example/admin", requires: "portal.dns,chybi.klic", stateLabel: "ověřit", system: "NIA",
                body: [Paragraph("Poznámka"), List("1. 11. 2026 vyžádáno")]),
            Block(B, "portal.dns", "DNS záznam", direction: "outbound", party: "Dodavatel", date: "2027-01-02"),
            Block(C, "", "", body: [Paragraph("Bez názvu")]));

        var conversion = Convert(json);

        Rules.ValidateDocument(conversion.Json);
        Assert.Null(RequirementHeadingConverter.RoundTripFailure(conversion.Json));
    }

    [Fact]
    public void What_markdown_never_told_apart_does_not_hold_a_page_back()
    {
        // Production 4.0.0 left eight order pages in blocks („Markdown round trip differs at block …“): a history line added on the web is a
        // list of its own right after the notes' list, and a recognized page name is a page reference, in a note or in the title.
        const string Reference = """{"type":"pageReference","attrs":{"id":"2a58a57e-3d36-49dd-af36-a2a4f9d9ded9","title":"Strukturované požadavky","url":"#/pages/2a58a57e-3d36-49dd-af36-a2a4f9d9ded9"}}""";
        static string Inline(params string[] inlines) => """{"type":"paragraph","content":[""" + string.Join(",", inlines) + "]}";
        static string Text(string text) => $$"""{"type":"text","text":"{{text}}"}""";
        string[] bodies =
        [
            Block(A, "a.b", "Registrace", body: [Paragraph("Kdo"), List("jedna", "dva"), List("3. 11. 2026 14:41 — Otestováno")]),
            Block(A, "a.b", "Registrace", body: [Paragraph("Historie"), List("30. 10. 2026 — A", "2. 11. 2026 — B"), List("3. 11. 2026 14:45 — C")]),
            Block(A, "a.b", "Termín", body: [Inline(Text("Stav: pro "), Reference, Text(" a úložiště."))]),
            Block(A, "a.b", "", body: []).Replace("""{"type":"paragraph"}""", Inline(Reference, Text(" a dotazníky fungují")), StringComparison.Ordinal),
        ];
        foreach (var body in bodies)
        {
            var conversion = Convert(LegacyDocumentRepair.Repair(Envelope(body)));

            Rules.ValidateDocument(conversion.Json);
            Assert.True(conversion.Report.Passes);
            Assert.Null(RequirementHeadingConverter.RoundTripFailure(conversion.Json));
        }

        // What Markdown does lose still holds the page back.
        var aligned = Convert(Envelope(Block(A, "a.b", "Termín", body: ["""{"type":"paragraph","attrs":{"textAlign":"center"},"content":[{"type":"text","text":"Na střed"}]}"""])));
        Assert.Equal("block 1: paragraph", RequirementHeadingConverter.RoundTripFailure(aligned.Json));
    }

    [Fact]
    public void The_gate_finds_a_lost_field_and_a_gained_one()
    {
        var conversion = Convert(Envelope(Block(A, "a", "Certifikát", party: "Jan", date: "2026-12-01")));
        var document = Document(conversion.Json);
        document["content"]!.AsArray().RemoveAt(1);

        document["content"]![0]!["attrs"]!.AsObject().Remove("party");

        var lost = RequirementConversionReport.Compare(Page, conversion.Report.Before, RequirementHeadingReader.Read(Page, document), conversion.Report.Cases);

        Assert.False(lost.Passes);
        Assert.Equal(new[] { "party", "deadline" }, lost.Losses.Select(x => x.Field));

        // A note that reads like a facts line becomes one: a gained deadline the conversion cannot tell apart, so the gate stops it.
        var gained = Convert(Envelope(Block(A, "a", "Certifikát", body: [Paragraph("do 1. 12. 2026")])));
        Assert.False(gained.Report.Passes);
        Assert.Equal(new[] { "deadline", "notes" }, gained.Report.Gains.Concat(gained.Report.Losses).Select(x => x.Field).Order());
        // An od/pro note stays a note (version 4): nothing is gained.
        Assert.True(Convert(Envelope(Block(A, "a", "Certifikát", body: [Paragraph("od pondělí řeší dodavatel")]))).Report.Passes);
    }

    internal static string Version3(params string[] blocks) => Envelope(blocks).Replace("\"version\":2}", "\"version\":3}", StringComparison.Ordinal);

    internal static string Heading3(string id, string text, string direction = "inbound")
        => $$"""{"type":"heading","attrs":{"level":2,"headingId":"{{id}}","state":"planned","direction":"{{direction}}","aliases":"{{id}}"},"content":[{"type":"text","text":"{{text}}"}]}""";

    internal static string ContactLine(string word, string name, string tail)
        => $$$"""{"type":"paragraph","content":[{"type":"text","text":"{{{word}}} "},{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"{{{name}}}","objectId":"{{{Contact}}}"}]""" + "}}}"
            + (tail.Length > 0 ? $$""",{"type":"text","text":"{{tail}}"}""" : "") + "]}";

    [Fact]
    public void A_version_3_page_moves_each_party_into_its_heading_and_keeps_the_deadline_and_the_notes()
    {
        var json = Version3(
            Heading3("a", "Kontakt s termínem"), ContactLine("od", "IT nemocnice", " · do 15. 11. 2026"), Paragraph("Poznámka A"),
            Heading3("b", "Kontakt bez termínu", "outbound"), ContactLine("pro", "IT nemocnice", ""), List("od Petr · do 1. 1. 2027"),
            Heading3("c", "Text s termínem"), Paragraph("od Jan Novák · do 2. 1. 2027"),
            Heading3("d", "Text bez termínu"), Paragraph("od Jan Novák"), Paragraph("od Eva · do 3. 1. 2027"),
            Heading3("e", "Jen termín"), Paragraph("do 4. 1. 2027"),
            Heading3("f", "Řádek navíc"), Paragraph("od Eva · telefonicky"));

        var conversion = Convert(json);

        Assert.True(conversion.Changed);
        var content = Content(conversion);
        Assert.Equal(4, (int)JsonNode.Parse(conversion.Json)!["plugins"]![0]!["version"]!);
        Assert.Equal(new (string?, string?)[] { ("IT nemocnice", Contact), ("IT nemocnice", Contact), ("Jan Novák", null), ("Jan Novák", null), (null, null), ("Eva", null) },
            content.Where(RequirementHeadingReader.IsRequirementHeading).Select(x => ((string?)x!["attrs"]!["party"], (string?)x["attrs"]!["partyContactId"])));
        // The deadline stays as the facts line, a line without one goes; the notes, the list and an od line further down stay as they were.
        Assert.Equal(new[]
        {
            "a", Paragraph("do 15. 11. 2026"), Paragraph("Poznámka A"),
            "b", List("od Petr · do 1. 1. 2027"),
            "c", Paragraph("do 2. 1. 2027"),
            "d", Paragraph("od Eva · do 3. 1. 2027"),
            "e", Paragraph("do 4. 1. 2027"),
            "f", Paragraph("od Eva · telefonicky"),
        }, content.Select(x => RequirementHeadingReader.IsRequirementHeading(x) ? (string)x!["attrs"]!["headingId"]! : Compact(x)));
        var report = conversion.Report;
        Assert.True(report.Passes);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report.BeforeCounts), System.Text.Json.JsonSerializer.Serialize(report.AfterCounts with { Notes = report.AfterCounts.Notes - 1 }));
        Assert.Equal((5, 2, 3), (report.AfterCounts.Parties, report.AfterCounts.PartiesWithContact, report.AfterCounts.Deadlines));
        // The line that said more stays as a note: a reported case, its gain accepted.
        Assert.Equal((RequirementCaseKind.FactsLineKept, "f"), (Assert.Single(report.Cases).Kind, report.Cases[0].BlockId));
        Assert.Equal(("notes", "facts line kept"), (Assert.Single(report.Gains).Field, report.Gains[0].Accepted));
        Rules.ValidateDocument(conversion.Json);
        Assert.Null(RequirementHeadingConverter.RoundTripFailure(conversion.Json));

        Assert.False(Convert(conversion.Json).Changed);
    }

    /// <summary>A heading made in 3.6 has no aliases: a kept line's case names its heading id. A party over the attribute's limit stays in
    /// its kept line under the deadline, the heading without a party, and the gate accepts that.</summary>
    [Fact]
    public void A_kept_line_names_its_heading_and_a_party_over_the_limit_stays_in_its_line()
    {
        static string Plain(string id) => $$"""{"type":"heading","attrs":{"level":2,"headingId":"{{id}}","state":"planned"},"content":[{"type":"text","text":"{{id}}"}]}""";
        var party = new string('A', 201);

        var conversion = Convert(Version3(Plain("a"), Paragraph("od Eva · telefonicky"), Plain("b"), Paragraph($"od {party} · do 1. 2. 2027"), Plain("c"), Paragraph("od Petr")));

        Assert.True(conversion.Report.Passes);
        Assert.Equal(new[] { "b", "a" }, conversion.Report.Cases.Where(x => x.Kind == RequirementCaseKind.FactsLineKept).Select(x => x.BlockId));
        Assert.Equal(new[] { "a", Paragraph("od Eva · telefonicky"), "b", Paragraph("do 1. 2. 2027"), Paragraph($"od {party} · do 1. 2. 2027"), "c" },
            Content(conversion).Select(x => RequirementHeadingReader.IsRequirementHeading(x) ? (string)x!["attrs"]!["headingId"]! : Compact(x)));
        Assert.Equal(new string?[] { "Eva", null, "Petr" }, Content(conversion).Where(RequirementHeadingReader.IsRequirementHeading).Select(x => (string?)x!["attrs"]!["party"]));
        Rules.ValidateDocument(conversion.Json);
    }

    [Fact]
    public void The_gate_counts_a_party_the_move_loses()
    {
        var conversion = Convert(Version3(Heading3("a", "A"), ContactLine("od", "IT nemocnice", " · do 15. 11. 2026")));
        var document = Document(conversion.Json);
        document["content"]![0]!["attrs"]!.AsObject().Remove("partyContactId");
        document["content"]![0]!["attrs"]!.AsObject().Remove("party");

        var lost = RequirementConversionReport.Compare(Page, conversion.Report.Before, RequirementHeadingReader.Read(Page, document), conversion.Report.Cases);

        Assert.True(conversion.Report.Passes);
        Assert.False(lost.Passes);
        Assert.Equal(new[] { "party", "partyContactId" }, lost.Losses.Select(x => x.Field));
    }
}
