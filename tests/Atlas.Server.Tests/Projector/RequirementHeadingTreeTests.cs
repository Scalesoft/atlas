using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Content;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// Requirement headings (shape 2026-11-04) through the readers and the block commands that read and write blocks: a heading reads as its
/// block, and a write changes the heading's attributes (its party too, §6), its dependency line, its facts line (the deadline) and its notes
/// in the canonical text. Domain only:
/// the editor schema does not know the heading's state yet (W2a), so no save goes through the validator here.
/// </summary>
public sealed class RequirementHeadingTreeTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly Guid PageId = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("00000000-0000-4000-8000-0000000000b2");

    /// <summary>MPI › Server › SSL (waiting on the network admins, to 15. 11.), DNS (needs SSL) and a note with a history line.</summary>
    private static JsonObject Page() => Rich(
        Heading("MPI", "mpi", 1),
        Heading("Server", "server", 2),
        RequirementHeading("portal.ssl", "SSL certifikát", "waiting_for_input", "inbound", party: "Správa sítě"),
        Paragraph("do 15. 11. 2026"),
        Paragraph("Certifikát vydá správa sítě."),
        new JsonObject { ["type"] = "bulletList", ["content"] = new JsonArray(new JsonObject { ["type"] = "listItem",
            ["content"] = new JsonArray(Paragraph("1. 11. 2026 — Urgováno: správa sítě")) }) },
        RequirementHeading("portal.dns", "DNS záznam"),
        Dependencies((PageId, "portal.ssl")),
        Heading("Klient", "klient", 2));

    private static JsonArray Content(JsonObject rich) => rich["document"]!["content"]!.AsArray();

    private static string Texts(JsonObject rich) => string.Join(" | ", Content(rich).Select(node => (node!["type"]!.GetValue<string>() == "heading"
        ? "#" + node["attrs"]!["level"] + (RequirementHeadings.IsRequirement(node) ? "[" + node["attrs"]!["state"]
            + (RequirementHeadings.PartyOf(node) is { Length: > 0 } party ? " " + party : "") + "]" : "") + " " : "")
        + (node["attrs"]?["dependencies"] is not null ? "Závislosti: " + string.Join(", ", RequirementHeadings.Links(node).Select(x => x.Page == PageId ? x.HeadingId : x.Page + "#" + x.HeadingId)) : Plain(node))));

    /// <summary>The text of a node and everything in it, a contact as its name.</summary>
    private static string Plain(JsonNode? node) => node?["type"]?.GetValue<string>() switch
    {
        "text" => node["text"]!.GetValue<string>(),
        "contactInline" => RequirementHeadings.Text(new JsonObject { ["content"] = new JsonArray(node.DeepClone()) }),
        _ => string.Concat((node?["content"] as JsonArray ?? []).Select(Plain)),
    };

    [Fact]
    public void A_heading_reads_as_its_block_with_its_section_summary_history_and_board()
    {
        var rich = Page();

        var blocks = RequirementTree.Read(rich, PageId);
        Assert.Equal(["portal.ssl", "portal.dns"], blocks.Select(x => x.BlockId));
        Assert.Equal(("2026-11-15", "Správa sítě", "inbound"), (blocks[0].Date, blocks[0].Party, blocks[0].Direction));
        Assert.Equal(["portal.ssl"], blocks[1].Requires);
        Assert.Equal(["portal.dns"], RequirementSummary.WithWaitingOn(blocks).Where(x => x.WaitingOn.Count > 0).Select(x => x.BlockId));
        // A requirement heading is no section: both are under MPI › Server.
        Assert.All(Atlas.Server.Features.Projector.Persistence.RequirementIndex.Sections(rich["document"]!).Values, x => Assert.Equal(["MPI", "Server"], x.Section));
        // The page overview's line (RequirementText.Summary) as the Markdown of the page writes it.
        Content(rich).Insert(0, Summary("s"));
        var summary = EditorMarkdownWriter.Write(rich["document"]!.AsObject()).Split('\n').Single(line => line.StartsWith("**Stav nasazení:**", StringComparison.Ordinal));
        Content(rich).RemoveAt(0);
        Assert.StartsWith("**Stav nasazení:** 0 % hotovo (0 z 2) · čeká na: SSL certifikát (Správa sítě) · nedodané vstupy: SSL certifikát (Správa sítě)", summary);
        Assert.Contains("čeká na 1 prerekvizitu: DNS záznam", summary);
        // Its notes are the section without the facts line; the history line is the requirement's.
        var view = RequirementTree.Find(rich, "portal.ssl", PageId);
        Assert.Equal(["Certifikát vydá správa sítě."], view["content"]!.AsArray().Skip(1).Where(x => x!["type"]!.GetValue<string>() == "paragraph").Select(x => RequirementHeadings.Text(x)));
        Assert.Equal(("2026-11-01", "Urgováno: správa sítě"), RequirementTree.History(view).Select(x => (x.Date, x.Text)).Single());
        // The board: a module is the plain heading that holds requirements.
        var map = DeploymentBoard.Of(rich["document"]);
        var module = map.Products.Single().Categories.Single().Modules.Single();
        Assert.Equal(("MPI", "Server", "portal.ssl,portal.dns"), (map.Products[0].Name, module.Name, string.Join(",", module.Blocks.Select(x => x.Id))));
    }

    [Fact]
    public void A_heading_page_is_a_catalog_of_its_requirement_headings()
    {
        var definitions = RequirementTree.Definitions(PageId, Page());

        Assert.Equal(["portal.ssl", "portal.dns"], definitions.Select(x => x.Key));
        var dns = definitions[1];
        Assert.Equal(("Server", "server", "MPI"), (dns.Bundle, dns.ModuleKey, dns.Category));
        Assert.Equal(["portal.ssl"], dns.Requires);
        Assert.Equal($"{PageId:D}#portal.dns", RequirementAttributes.Text(dns.ContentJson!["attrs"], "from"));
    }

    [Fact]
    public void An_update_writes_the_heading_the_facts_line_the_dependency_line_and_where()
    {
        var rich = Page();
        var contact = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";

        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Title: "DNS a reverzní záznam", State: "blocked", Direction: "outbound",
            System: "NIA", StateLabel: "čeká na DNS", Party: "Jan Novák", PartyContactId: contact, Date: "2026-12-01", Where: "Keepass › KBN",
            Requires: ["portal.ssl", "infra.vpn@" + Other.ToString("D")]), Today, PageId);

        Assert.Equal("#1 MPI | #2 Server | #3[waiting_for_input Správa sítě] SSL certifikát | do 15. 11. 2026 | Certifikát vydá správa sítě. | "
            + "1. 11. 2026 — Urgováno: správa sítě | #3[blocked Jan Novák] DNS a reverzní záznam | Závislosti: portal.ssl, " + Other + "#infra.vpn | "
            + "do 1. 12. 2026 | Kde uloženo: Keepass › KBN | #2 Klient", Texts(rich));
        var heading = Content(rich)[6]!["attrs"]!;
        Assert.Equal(contact, heading["partyContactId"]!.GetValue<string>());
        Assert.Equal(("outbound", "NIA", "čeká na DNS"), (heading["direction"]!.GetValue<string>(), heading["system"]!.GetValue<string>(), heading["stateLabel"]!.GetValue<string>()));
        var read = RequirementTree.Read(rich, PageId)[1];
        Assert.Equal((contact, "Jan Novák", "2026-12-01", "portal.ssl," + Other + "#infra.vpn"), (read.PartyContactId, read.Party, read.Date, string.Join(",", read.Requires)));
        Assert.Equal("SSL certifikát", Content(rich)[7]!["content"]![0]!["attrs"]!["title"]!.GetValue<string>());

        // Cleared: no party, no facts line, no where, no dependency line; done without a date is dated today.
        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Party: "", PartyContactId: "", Date: "", Where: "", Requires: []), Today, PageId);
        Assert.Equal((null, null), ((string?)Content(rich)[6]!["attrs"]!["party"], (string?)Content(rich)[6]!["attrs"]!["partyContactId"]));
        RequirementTree.Update(rich, "portal.dns", new RequirementChange(State: "done"), Today, PageId);
        Assert.EndsWith("#3[done] DNS a reverzní záznam | hotovo 4. 11. 2026 | #2 Klient", Texts(rich));
        Assert.Equal(("", "2026-11-04"), (RequirementTree.Read(rich, PageId)[1].Date, RequirementTree.Read(rich, PageId)[1].Completed));
        // The deadline is written beside it, and leaving done takes the day off (3.5's „hotovo“, kept in 3.6).
        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Date: "2026-12-01"), Today, PageId);
        Assert.EndsWith("#3[done] DNS a reverzní záznam | do 1. 12. 2026 · hotovo 4. 11. 2026 | #2 Klient", Texts(rich));
        RequirementTree.Update(rich, "portal.dns", new RequirementChange(State: "in_progress"), Today, PageId);
        Assert.EndsWith("#3[in_progress] DNS a reverzní záznam | do 1. 12. 2026 | #2 Klient", Texts(rich));
    }

    [Fact]
    public void A_new_id_keeps_the_former_one_and_the_page_links_follow_it()
    {
        var rich = Page();
        // An old key a client still sends resolves through the aliases.
        Content(rich)[2]!["attrs"]!["aliases"] = "b-17,ssl-old";

        var warnings = RequirementTree.Update(rich, "portal.ssl", new RequirementChange(Key: "portal.tls"), Today, PageId);

        Assert.Contains("„DNS záznam“", Assert.Single(warnings));
        Assert.Equal(("portal.tls", "b-17,ssl-old,portal.ssl"), (RequirementHeadings.HeadingId(Content(rich)[2]), Content(rich)[2]!["attrs"]!["aliases"]!.GetValue<string>()));
        Assert.Equal(["portal.tls"], RequirementTree.Read(rich, PageId)[1].Requires);
        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Requires: ["ssl-old"]), Today, PageId);
        Assert.Equal(["portal.tls"], RequirementTree.Read(rich, PageId)[1].Requires);
        // The former id still finds it.
        Assert.Equal("portal.tls", RequirementTree.Key(RequirementTree.Find(rich, "portal.ssl", PageId)));
        var taken = Assert.Throws<ApiException>(() => RequirementTree.Update(rich, "portal.dns", new RequirementChange(Key: "server"), Today, PageId));
        Assert.Equal("requirement_key_taken", taken.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_title_is_refused_and_the_heading_keeps_its_own(string title)
    {
        // A requirement heading is its title: no write leaves one without it (third review P3-1, as the editor's Enter).
        var rich = Page();
        var before = Texts(rich);

        var error = Assert.Throws<ApiException>(() => RequirementTree.Update(rich, "portal.dns", new RequirementChange(Title: title), Today, PageId));

        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal(before, Texts(rich));
    }

    [Fact]
    public void Notes_replace_the_section_but_the_facts_and_dependency_lines()
    {
        var rich = Page();
        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Party: "IT KBN"), Today, PageId);

        RequirementTree.Update(rich, "portal.dns", new RequirementChange(NotesMarkdown: "Záznam založí IT.\n\n- 2. 11. 2026 — Zadáno"), Today, PageId);

        Assert.EndsWith("#3[planned IT KBN] DNS záznam | Závislosti: portal.ssl | Záznam založí IT. | 2. 11. 2026 — Zadáno | #2 Klient", Texts(rich));
    }

    [Fact]
    public void An_update_writes_the_lines_at_their_fixed_places_and_a_joined_section_keeps_its_stray_lines_as_notes()
    {
        // A join: the lines of a removed heading's requirement sit under DNS's own facts line.
        var rich = Rich(RequirementHeading("portal.ssl", "SSL"), RequirementHeading("portal.dns", "DNS", party: "IT KBN"), Paragraph("do 5. 5. 2026"), Paragraph("Poznámka"),
            Dependencies((PageId, "portal.ssl")), Paragraph("do 1. 1. 2027"));
        Assert.Equal(("IT KBN", "2026-05-05", ""), Read(rich));

        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Party: "", PartyContactId: "", Date: ""), Today, PageId);

        // Its facts line gone, the note heads the section; the stray line's flag is cleared, so it never becomes the dependency line.
        Assert.EndsWith("#3[planned] DNS | Poznámka |  | do 1. 1. 2027", Texts(rich));
        Assert.Null(Content(rich)[3]!["attrs"]!["dependencies"]);
        Assert.Equal(("", "", ""), Read(rich));

        RequirementTree.Update(rich, "portal.dns", new RequirementChange(Requires: ["portal.ssl"], Date: "2026-12-01"), Today, PageId);

        Assert.EndsWith("#3[planned] DNS | Závislosti: portal.ssl | do 1. 12. 2026 | Poznámka |  | do 1. 1. 2027", Texts(rich));
        Assert.Equal(("", "2026-12-01", "portal.ssl"), Read(rich));

        (string Party, string Date, string Requires) Read(JsonObject document)
            => RequirementTree.Read(document, PageId)[1] is var x ? (x.Party, x.Date, string.Join(",", x.Requires)) : default;
    }

    [Fact]
    public void Insert_move_and_remove_work_on_heading_sections()
    {
        var rich = Page();
        var catalog = RequirementTree.Definitions(Other, Rich(Heading("Klient", "k", 2), RequirementHeading("client.app", "Aplikace", "done", party: "Dodavatel"),
            Dependencies((Other, "portal.ssl")), Paragraph("do 1. 1. 2026"), Paragraph("Instalace na stanice.")));

        var inserted = RequirementTree.Insert(rich, [catalog[0].ContentJson!], new RequirementPosition(HeadingId: "klient"), PageId);

        Assert.Equal("client.app", RequirementAttributes.Text(inserted[0]["attrs"], "id"));
        // A copy: planned, no party and date; one level under Klient; its dependency on the catalog's own heading is this page's now.
        Assert.EndsWith("#2 Klient | #3[planned] Aplikace | Závislosti: portal.ssl | Instalace na stanice.", Texts(rich));
        Assert.Equal($"{Other:D}#client.app", Content(rich)[^3]!["attrs"]!["from"]!.GetValue<string>());

        RequirementTree.Move(rich, "client.app", new RequirementPosition(AfterBlockId: "portal.ssl"));
        Assert.Equal(["portal.ssl", "client.app", "portal.dns"], RequirementTree.Read(rich, PageId).Select(x => x.BlockId));
        Assert.Equal("invalid_position", Assert.Throws<ApiException>(() => RequirementTree.Move(rich, "client.app", new RequirementPosition(IntoBlockId: "portal.ssl"))).Code);

        RequirementTree.Remove(rich, "portal.ssl");
        Assert.Equal("#1 MPI | #2 Server | #3[planned] Aplikace | Závislosti: portal.ssl | Instalace na stanice. | #3[planned] DNS záznam | Závislosti: portal.ssl | #2 Klient", Texts(rich));
        // What depends on the removed one waits on a requirement the page lacks.
        Assert.Contains("„portal.ssl“", Assert.Single(RequirementTree.MissingPrerequisites(rich, pageId: PageId)));
    }

    [Fact]
    public void A_copy_starts_the_headings_afresh_and_its_links_lead_to_the_copy()
    {
        var rich = Page();
        Content(rich)[2]!["attrs"]!["aliases"] = "b-17";
        Content(rich)[2]!["attrs"]!["stateLabel"] = "urgovat";
        Content(rich).Insert(5, Paragraph(RequirementHeadings.WhereLabel + "Keepass"));

        RequirementTree.ResetForCopy(rich);

        Assert.StartsWith("#1 MPI | #2 Server | #3[planned] SSL certifikát | Certifikát vydá správa sítě. | #3[planned] DNS záznam", Texts(rich));
        Assert.Equal("", Content(rich)[2]!["attrs"]!["stateLabel"]!.GetValue<string>());
        Assert.Null(Content(rich)[2]!["attrs"]!["aliases"]);
        Assert.Null(Content(rich)[2]!["attrs"]!["party"]);
        var copy = Guid.NewGuid();
        var json = RequirementHeadings.Rehome(rich.ToJsonString(), PageId, copy);
        Assert.Equal([(copy, "portal.ssl")], RequirementHeadings.Links(JsonNode.Parse(json)!["document"]!["content"]![5]));
    }

    [Fact]
    public void Normalize_gives_a_requirement_heading_without_an_id_one()
    {
        var rich = Rich(Heading("Server", "server", 2), RequirementHeading("server", "Server"), RequirementHeading("", "Zálohy"));
        Content(rich)[2]!["attrs"]!.AsObject().Remove("headingId");

        RequirementTree.Normalize(rich);

        Assert.Equal(["server", "server-2", "zalohy"], Content(rich).Select(RequirementHeadings.HeadingId));
    }

    [Fact]
    public void Cross_page_references_of_both_shapes_name_their_page()
    {
        Assert.Equal(("infra.vpn", Other), RequirementAttributes.CrossPage($"{Other:D}#infra.vpn")!.Value);
        Assert.Equal(("infra.vpn", Other), RequirementAttributes.CrossPage($"infra.vpn@{Other:D}")!.Value);
        Assert.Null(RequirementAttributes.CrossPage("infra.vpn"));
        Assert.Null(RequirementAttributes.CrossPage($"{Other:D}#-bad"));
    }
}
