using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// The five shapes Atlas 2.x stored and rendered that the editor schema refused (UX cycle 2, IM2), synthetic text only. The editor's
/// copy of the rules is src/Atlas.Markdown/src/engine/legacyDocument.ts (legacyDocument.test.ts holds the same shapes).
/// </summary>
public sealed class LegacyDocumentRepairTests
{
    private const string Link = """{"type":"link","attrs":{"href":"https://example.com/HREF","target":"_blank","rel":"noopener noreferrer"}}""";

    private static string Envelope(string content) =>
        $$$"""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{{{content}}}]}}""";

    private static readonly string NestedLink = Envelope($$"""{"type":"paragraph","content":[{"type":"text","text":"badge","marks":[{{Link.Replace("HREF", "outer")}},{{Link.Replace("HREF", "inner")}}]}]}""");
    private static readonly string BoldCode = Envelope("""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"bold"},{"type":"code"}]}]}""");
    private static readonly string ItalicCode = Envelope("""{"type":"paragraph","content":[{"type":"text","text":"y","marks":[{"type":"italic"},{"type":"code"}]}]}""");
    private static readonly string ItemStartingWithList = Envelope("""
        {"type":"orderedList","attrs":{"start":12},"content":[{"type":"listItem","content":[
          {"type":"orderedList","attrs":{"start":12},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"inner"}]}]}]}]}]}
        """);
    private static readonly string EmptyItem = Envelope("""{"type":"orderedList","attrs":{"start":1},"content":[{"type":"listItem"}]}""");

    public static TheoryData<string> Refused => new() { nameof(NestedLink), nameof(ItemStartingWithList), nameof(EmptyItem) };

    private static string Shape(string name) => name switch
    {
        nameof(NestedLink) => NestedLink, nameof(BoldCode) => BoldCode, nameof(ItalicCode) => ItalicCode,
        nameof(ItemStartingWithList) => ItemStartingWithList, _ => EmptyItem,
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_refused_shape_is_valid_after_the_repair(string name)
    {
        Assert.NotNull(MarkdownDocumentValidator.Builtin.Validate(Shape(name)));
        var changes = new List<LegacyDocumentChange>();
        var repaired = LegacyDocumentRepair.Repair(Shape(name), changes);
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(repaired));
        Assert.NotEmpty(changes);
    }

    [Theory]
    [InlineData(nameof(BoldCode))]
    [InlineData(nameof(ItalicCode))]
    public void Inline_code_with_bold_or_italic_is_valid_as_it_is(string name)
    {
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(Shape(name)));
        Assert.Same(Shape(name), LegacyDocumentRepair.Repair(Shape(name)));
    }

    [Fact]
    public void A_link_in_a_link_keeps_the_inner_link_and_reports_the_outer_one()
    {
        var changes = new List<LegacyDocumentChange>();
        var repaired = JsonNode.Parse(LegacyDocumentRepair.Repair(NestedLink, changes))!;
        var marks = repaired["document"]!["content"]![0]!["content"]![0]!["marks"]!.AsArray();
        Assert.Equal("https://example.com/inner", Assert.Single(marks)!["attrs"]!["href"]!.GetValue<string>());
        var change = Assert.Single(changes);
        Assert.Equal(("document.content[0].content[0].marks[0]", LegacyDocumentRepair.RepeatedMark), (change.Path, change.Code));
        Assert.Contains("https://example.com/outer", change.Removed, StringComparison.Ordinal);
    }

    /// <summary>Atlas 4.0: a stored target, rel or class the editor does not render is dropped on read (legacyDocument.test.ts); it stays
    /// valid, as live documents hold pasted values.</summary>
    [Fact]
    public void A_link_loses_a_target_rel_or_class_the_editor_does_not_render()
    {
        var odd = Envelope("""{"type":"paragraph","content":[{"type":"text","text":"odkaz","marks":[{"type":"link","attrs":{"href":"https://example.com/","target":"_top","rel":"opener","class":"button","title":"T"}}]}]}""");
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(odd));
        var changes = new List<LegacyDocumentChange>();
        var repaired = LegacyDocumentRepair.Repair(odd, changes);
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(repaired));
        Assert.Equal("""{"href":"https://example.com/","title":"T"}""", JsonNode.Parse(repaired)!["document"]!["content"]![0]!["content"]![0]!["marks"]![0]!["attrs"]!.ToJsonString());
        Assert.Equal(("document.content[0].content[0].marks[0]", LegacyDocumentRepair.LinkAttributes), (Assert.Single(changes).Path, changes[0].Code));
        // What the editor writes stays as it is.
        var plain = Envelope($$"""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{{Link.Replace("HREF", "a")}}]}]}""");
        Assert.Same(plain, LegacyDocumentRepair.Repair(plain));
    }

    [Fact]
    public void A_list_item_gets_an_empty_leading_paragraph_and_keeps_its_content()
    {
        var item = JsonNode.Parse(LegacyDocumentRepair.Repair(ItemStartingWithList))!["document"]!["content"]![0]!["content"]![0]!;
        Assert.Equal(["paragraph", "orderedList"], item["content"]!.AsArray().Select(x => x!["type"]!.GetValue<string>()));
        Assert.Null(item["content"]![0]!["content"]);
        var empty = JsonNode.Parse(LegacyDocumentRepair.Repair(EmptyItem))!["document"]!["content"]![0]!["content"]![0]!;
        Assert.Equal("""{"type":"listItem","content":[{"type":"paragraph"}]}""", empty.ToJsonString());
    }

    [Fact]
    public void The_deployment_summary_reads_as_the_requirements_overview_at_any_depth()
    {
        var changes = new List<LegacyDocumentChange>();
        var repaired = JsonNode.Parse(LegacyDocumentRepair.Repair(Envelope("""
            {"type":"requirementSummary","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000001"}},
            {"type":"blockquote","content":[{"type":"requirementSummary"}]}
            """), changes))!["document"]!["content"]!;
        Assert.Equal("""{"type":"overview","attrs":{"id":"6f1c1d3e-8a4b-4c55-9d1e-000000000001","kind":"requirements"}}""", repaired[0]!.ToJsonString());
        Assert.Equal("""{"type":"overview","attrs":{"kind":"requirements"}}""", repaired[1]!["content"]![0]!.ToJsonString());
        Assert.Equal([("document.content[0]", LegacyDocumentRepair.SummaryOverview), ("document.content[1].content[0]", LegacyDocumentRepair.SummaryOverview)],
            changes.Select(change => (change.Path, change.Code)));
    }

    /// <summary>A history line as MarkdownToRich read „- D. M. 2026 …“ before 3.5.1: a list item that starts with the day's ordered list,
    /// whose one item starts with the month's, whose one item holds the rest of the line.</summary>
    private static string Dated(int day, int month, string inlines) => $$$"""
        {"type":"orderedList","attrs":{"tight":true,"start":{{{day}}}},"content":[{"type":"listItem","content":[{"type":"paragraph"},
          {"type":"orderedList","attrs":{"tight":true,"start":{{{month}}}},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{{{inlines}}}]}]}]}]}]}
        """;

    private static string Requirement(string content) =>
        $$$"""{"type":"requirement","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f7"},"content":[{"type":"paragraph","content":[{"type":"text","text":"VPN"}]},{{{content}}}]}""";

    [Fact]
    public void A_history_line_read_as_nested_ordered_lists_is_one_dated_line_again_only_inside_a_requirement()
    {
        var line = Dated(30, 10, """{"type":"text","text":"2026 23:40 — "},{"type":"text","text":"Urgováno","marks":[{"type":"bold"}]},{"type":"text","text":": IT KBN"}""");
        var marked = Dated(1, 11, """{"type":"text","text":"2026 — telefonát","marks":[{"type":"italic"}]}""");
        var steps = Dated(2, 3, """{"type":"text","text":"krok"}""");
        var changes = new List<LegacyDocumentChange>();
        var repaired = JsonNode.Parse(LegacyDocumentRepair.Repair(Envelope(
            Requirement($$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{line}}]}]},{{marked}},{{steps}}""") + "," + line),
            changes))!["document"]!["content"]!;
        static string Raw(JsonNode? node) => node!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        // The line repaired, its list starts with a stamp: a timed list (rich revision 5).
        Assert.Equal("""
            {"type":"timedList","content":[{"type":"timedItem","content":[{"type":"paragraph","content":[{"type":"text","text":"30. 10. 2026 23:40 — "},{"type":"text","text":"Urgováno","marks":[{"type":"bold"}]},{"type":"text","text":": IT KBN"}]}]}]}
            """, Raw(repaired[0]!["content"]![1]));
        Assert.Equal("""
            {"type":"paragraph","content":[{"type":"text","text":"1. 11. "},{"type":"text","text":"2026 — telefonát","marks":[{"type":"italic"}]}]}
            """, Raw(repaired[0]!["content"]![2]));
        // Another nested list (no year after the month) and the same shape outside a requirement stay as they are.
        Assert.Equal(Raw(JsonNode.Parse(steps)), Raw(repaired[0]!["content"]![3]));
        Assert.Equal(Raw(JsonNode.Parse(line)), Raw(repaired[1]));
        Assert.Equal([("document.content[0].content[1].content[0].content[0]", LegacyDocumentRepair.HistoryDateList), ("document.content[0].content[1]", LegacyDocumentRepair.TimedList),
            ("document.content[0].content[2]", LegacyDocumentRepair.HistoryDateList)], changes.Select(change => (change.Path, change.Code)));
        var once = LegacyDocumentRepair.Repair(Envelope(Requirement(line)));
        Assert.Same(once, LegacyDocumentRepair.Repair(once));
    }

    /// <summary>The editor's legacyDocument.test.ts runs the same cases: an attribute title, a title node, empty titles, nested ones and one
    /// at the document start each become a heading and the body after it.</summary>
    public static TheoryData<string> LegacyDisclosures() =>
        new(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "document-schema", "legacy-disclosures.json")))!
            .AsArray().Select(entry => entry!["name"]!.GetValue<string>()));

    [Theory]
    [MemberData(nameof(LegacyDisclosures))]
    public void A_disclosure_is_a_heading_and_its_body_as_the_editor_repairs_it(string name)
    {
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "document-schema", "legacy-disclosures.json")))!
            .AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var changes = new List<LegacyDocumentChange>();
        var stored = $$"""{"format":"atlas-rich","version":1,"document":{{entry["before"]!.ToJsonString()}}}""";
        var repaired = LegacyDocumentRepair.Repair(stored, changes);

        Assert.True(JsonNode.DeepEquals(entry["after"], JsonNode.Parse(repaired)!["document"]), repaired);
        Assert.All(changes, change => Assert.Equal(LegacyDocumentRepair.DisclosureHeading, change.Code));
        Assert.NotEmpty(changes);
        Assert.Same(repaired, LegacyDocumentRepair.Repair(repaired));
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(repaired));
    }

    [Fact]
    public void A_bullet_list_starting_with_a_stamp_is_a_timed_list_on_any_page_its_attributes_kept()
    {
        static string List(string first, bool tight = true) => $$"""
            {"type":"bulletList","attrs":{"tight":{{(tight ? "true" : "false")}}},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"{{first}}"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"later"}]}]}]}
            """;
        var changes = new List<LegacyDocumentChange>();
        var content = JsonNode.Parse(LegacyDocumentRepair.Repair(Envelope(List("3. 11. 2026 17:19 — a", tight: false) + "," + List("3. 11. 2026 —") + "," + List("plain")), changes))!["document"]!["content"]!;

        Assert.Equal("""{"type":"timedList","attrs":{"tight":false},"content":[{"type":"timedItem","content":[{"type":"paragraph","content":[{"type":"text","text":"3. 11. 2026 17:19 — a"}]}]},{"type":"timedItem","content":[{"type":"paragraph","content":[{"type":"text","text":"later"}]}]}]}""",
            content[0]!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        // A fresh stamp with no text yet counts; a list whose first item has none stays a bullet list.
        Assert.Equal("timedList", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("bulletList", content[2]!["type"]!.GetValue<string>());
        Assert.Equal([("document.content[0]", LegacyDocumentRepair.TimedList), ("document.content[1]", LegacyDocumentRepair.TimedList)], changes.Select(change => (change.Path, change.Code)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":"atlas-rich","version":1}""")]
    [InlineData("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Žluťoučký"}]}]}}""")]
    public void Anything_else_is_returned_unchanged(string json) => Assert.Same(json, LegacyDocumentRepair.Repair(json));

    [Fact]
    public void Repaired_text_stays_raw_utf8() =>
        Assert.Contains("\"Žluť\"", LegacyDocumentRepair.Repair(Envelope("""
            {"type":"bulletList","content":[{"type":"listItem"}]},{"type":"paragraph","content":[{"type":"text","text":"Žluť"}]}
            """)), StringComparison.Ordinal);
}
