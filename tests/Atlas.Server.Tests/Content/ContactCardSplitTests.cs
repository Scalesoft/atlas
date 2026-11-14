using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>One contact per block (owner 2026-10-30): the content migration splits a card of several contacts into consecutive single-contact cards wherever it is.</summary>
public sealed class ContactCardSplitTests
{
    private const string Envelope = """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2}],"document":{"type":"doc","content":[""";

    internal static string Card(params string[] names) =>
        """{"type":"contactCard","attrs":{"data":{"version":1,"contacts":["""
        + string.Join(",", names.Select(name => $$"""{"name":"{{name}}","role":"R {{name}}","objectId":"0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b","kind":"internal","username":"{{name.ToLowerInvariant()}}"}"""))
        + "]}}}";

    internal static string Doc(params string[] blocks) => Envelope + string.Join(",", blocks) + "]}}";

    private const string Inline = """{"type":"paragraph","content":[{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Eva"}]}}}]}""";

    [Fact]
    public void A_card_of_several_contacts_becomes_consecutive_cards_at_every_depth_in_order()
    {
        var quoted = $$"""{"type":"blockquote","content":[{{Card("Olga", "Karel")}}]}""";
        var list = $$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{Card("Ivo", "Zdena")}}]}]}""";
        var json = Doc("""{"type":"paragraph","content":[{"type":"text","text":"Lidé"}]}""", Card("Jana", "Petr", "Eva"), quoted, list, Card("Solo"), Inline);

        var changes = new List<LegacyDocumentChange>();
        var split = LegacyDocumentRepair.Repair(json, changes);

        Assert.Equal(Doc("""{"type":"paragraph","content":[{"type":"text","text":"Lidé"}]}""", Card("Jana"), Card("Petr"), Card("Eva"),
            $$"""{"type":"blockquote","content":[{{Card("Olga")}},{{Card("Karel")}}]}""",
            $$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{Card("Ivo")}},{{Card("Zdena")}}]}]}""",
            Card("Solo"), Inline), split);
        // Valid as a page, deterministic and idempotent.
        Rules.ValidateDocument(split);
        Assert.Equal(["document.content[1]", "document.content[2].content[0]", "document.content[3].content[0].content[1]"],
            changes.Select(change => change.Path).Order(StringComparer.Ordinal));
        Assert.All(changes, change => Assert.Equal(LegacyDocumentRepair.SplitCard, change.Code));
        Assert.Equal(split, LegacyDocumentRepair.Repair(json));
        Assert.Same(split, LegacyDocumentRepair.Repair(split));
        // Every server read applies it.
        Assert.Equal(JsonNode.Parse(split)!["document"]!.ToJsonString(), RichDocument.Parse(json).Document.ToJsonString());
    }

    [Fact]
    public void A_document_without_a_card_of_several_and_an_unreadable_one_come_back_as_they_are()
    {
        foreach (var json in new[] { Doc(Card("Jana"), Inline), "{\"contactCard\":", "{\"document\":\"contactCard\"}" })
        {
            Assert.Same(json, LegacyDocumentRepair.Repair(json));
        }
    }

    [Fact]
    public void A_card_without_a_parent_stays_one_node()
    {
        var multi = JsonNode.Parse(Card("Jana", "Petr"))!.AsObject();
        Assert.False(LegacyDocumentRepair.Repair(multi));
        Assert.Equal(Card("Jana", "Petr"), multi.ToJsonString());
    }
}
