using System.Text.Json.Nodes;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Platform.Validation;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Linking;

/// <summary>
/// Seen when an agent linked the company wiki after the 3.2 release: a page from the 2.x import (a link nested in a link) failed the
/// validation of every linking write, and the people of a contact card stayed unresolved with one candidate (reference_split).
/// </summary>
public sealed class ContentReferenceRepairTests
{
    private static readonly Guid Manual = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Jana = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    [Fact]
    public void A_page_with_a_repeated_link_mark_links_as_the_editor_opens_it()
    {
        const string Nested = """{"type":"paragraph","content":[{"type":"text","text":"Hub","marks":[{"type":"link","attrs":{"href":"https://old.example"}},{"type":"link","attrs":{"href":"https://hub.example"}}]}]}""";
        var stored = Rich(Paragraph("Viz Provozní příručka."), Nested);
        Assert.NotNull(HostDocumentValidator.Instance.Validate(stored));

        var (root, occurrences) = ContentReferences.Read(stored, Lexicon());
        var mention = Assert.Single(occurrences);
        Assert.True(ContentReferences.Link(mention, ContentReferences.PageChip(Lexicon().Entities["page:" + Manual])));

        var written = ContentReferences.Write(root);
        Assert.Null(HostDocumentValidator.Instance.Validate(written));
        Assert.Contains("https://hub.example", written, StringComparison.Ordinal);
        Assert.DoesNotContain("https://old.example", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_person_of_a_contact_card_is_picked_and_the_card_keeps_its_details()
    {
        const string Card = """{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková","emails":["jana@example.com"],"unresolved":true}]}}}""";
        var (root, occurrences) = ContentReferences.Read(Rich(Card), Lexicon());
        var jana = Assert.Single(occurrences);
        Assert.Equal(("unresolved", "Jana Nováková"), (jana.State, jana.Text));

        Assert.True(ContentReferences.Link(jana, ContentReferences.PersonChip(Lexicon().Entities["object:" + Jana])));

        var written = ContentReferences.Write(root);
        Assert.Null(HostDocumentValidator.Instance.Validate(written));
        var contact = JsonNode.Parse(written)!["document"]!["content"]![0]!["attrs"]!["data"]!["contacts"]![0]!.AsObject();
        Assert.Equal(("Jana Nováková", Jana.ToString(), "jana@example.com", false), (contact["name"]!.GetValue<string>(), contact["objectId"]!.GetValue<string>(),
            contact["emails"]![0]!.GetValue<string>(), contact.ContainsKey("unresolved")));
        Assert.Equal("linked", Assert.Single(ContentReferences.Read(written, Lexicon()).Occurrences).State);
    }

    [Fact]
    public void A_chip_in_a_stored_disclosure_title_is_read_as_in_the_heading_the_repair_makes_of_it()
    {
        var disclosure = $$$"""
            {"type":"disclosure","content":[{"type":"disclosureTitle","content":[{"type":"pageReference","attrs":{"id":"{{{Manual}}}","title":"Provozní příručka","url":"/#/pages/{{{Manual}}}"}},{"type":"text","text":" a Jana Nováková"}]},{"type":"paragraph"}]}
            """;
        var occurrence = Assert.Single(ContentReferences.Read(Rich(disclosure), Lexicon()).Occurrences);
        // Read repaired (Atlas 3.6 has no disclosure): the chip is linked; the name in the heading is not offered as a mention (not prose).
        Assert.Equal(("page", "linked", Manual), (occurrence.Kind, occurrence.State, occurrence.TargetId));
    }

    private static PlaceLexicon Lexicon() => new("test",
        new RecognitionLexicon(false, [new RecognitionPerson("object:" + Jana, Jana, null, "Jana Nováková", [])], [new RecognitionPage(Manual, "Provozní příručka")], []),
        new Dictionary<string, PlaceEntity>
        {
            ["object:" + Jana] = new("object:" + Jana, "person", Jana, "Jana Nováková", "Modelsoft"),
            ["page:" + Manual] = new("page:" + Manual, "page", Manual, "Provozní příručka", null),
        });
}
