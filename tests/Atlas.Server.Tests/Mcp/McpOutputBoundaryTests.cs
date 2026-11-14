using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Publishing;
using Atlas.Server.Platform.Content;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

public sealed class McpOutputBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public void A_long_single_paragraph_pages_without_losing_or_splitting_Unicode(string prefix)
    {
        var markdown = prefix + string.Concat(Enumerable.Repeat("😀", 21_000));
        var page = new PageDto(Guid.NewGuid(), Guid.NewGuid(), null, "Guide", 1, true, "{}", markdown, 1, DateTime.UtcNow, null, true);
        var read = new McpPageRead(page, false, 1, "Wiki", "Wiki / Guide", new Dictionary<Guid, string>());
        var projection = new ContentProjection(markdown, [], [], false, []);
        var reconstructed = new StringBuilder();
        var strictUtf8 = new UTF8Encoding(false, true);
        string? cursor = null;
        var parts = 0;
        do
        {
            var result = McpPageOutput.Render(read, projection, null, cursor);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.True(text.Length <= McpText.Limit);
            var opening = $"<wiki-page id=\"{page.Id}\" version=\"published\">\n";
            var start = text.IndexOf(opening, StringComparison.Ordinal) + opening.Length;
            var end = text.IndexOf("\n</wiki-page>", start, StringComparison.Ordinal);
            var body = text[start..end];
            Assert.NotEmpty(body);
            _ = strictUtf8.GetBytes(body); // An isolated surrogate is not valid output, even if concatenation could repair it.
            reconstructed.Append(body);
            var structured = result.StructuredContent!.Value;
            cursor = structured.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            Assert.True(++parts <= 3, "Pagination must advance to the end.");
        } while (cursor is not null);
        Assert.Equal(2, parts);
        Assert.Equal(markdown, reconstructed.ToString());
    }

    /// <summary>Review S2-2: the components' readable text is budgeted in the structured content as in the text, and a window
    /// reserves room only for the text of the components it shows.</summary>
    [Fact]
    public void The_readable_text_of_components_is_budgeted_in_both_outputs_and_reserved_only_for_the_window()
    {
        var crowded = Render(string.Concat(Enumerable.Range(1, 300).Select(i => $"⟦c:{i}⟧\n\n")), Enumerable.Range(1, 300));
        var structured = System.Text.Json.Nodes.JsonNode.Parse(crowded.StructuredContent!.Value.GetRawText())!.AsObject();
        structured.Remove("text");
        // 300 components' facts (index, type, label) and the readable text the result shows (at most 12 000 characters), within a
        // result's limit and the frame's reserve; it was 330 KB.
        Assert.InRange(structured["components"]!.AsArray().Sum(x => x!["text"]?.GetValue<string>().Length ?? 0), 1, 12_000);
        Assert.True(structured.ToJsonString().Length <= McpText.Limit + 3_000, $"structured content without its text: {structured.ToJsonString().Length}");

        // Components only at the end of a long page: the first window is not shortened for their text.
        var lines = string.Concat(Enumerable.Range(0, 2_000).Select(i => $"Řádek {i:D4} prostého textu stránky.\n"));
        var late = Render(lines + string.Concat(Enumerable.Range(1, 30).Select(i => $"⟦c:{i}⟧\n\n")), Enumerable.Range(1, 30));
        var text = Assert.IsType<TextContentBlock>(Assert.Single(late.Content)).Text;
        var body = text[text.IndexOf("version=\"published\">\n", StringComparison.Ordinal)..text.IndexOf("\n</wiki-page>", StringComparison.Ordinal)];
        Assert.True(body.Length > 30_000, $"first window {body.Length}");
    }

    /// <summary>Review S3-1: the component lines and the notes beside them are charged to the budget too; a list that does not fit
    /// stops and says where the rest is, and the structured content carries only what the text lists.</summary>
    [Fact]
    public void Three_hundred_components_with_long_labels_and_embeds_stay_within_one_result_in_both_outputs()
    {
        var markdown = string.Concat(Enumerable.Range(1, 300).Select(i => $"⟦c:{i}⟧\n\n"));
        var embeds = Enumerable.Range(1, 3).Select(i => new ComponentEmbed(Guid.NewGuid(), EmbedKind.Image, i)).ToArray();
        var result = Render(markdown, Enumerable.Range(1, 300), slot => slot with { Label = new string('Ž', 200), Embeds = embeds });

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(text.Length <= McpText.Limit, $"text {text.Length}");
        Assert.Contains("projector_get_requirements", text[text.IndexOf("</wiki-page>", StringComparison.Ordinal)..]);
        Assert.Contains("komponent se sem nevešlo", text);
        // The structured content repeats the text (McpText.Result); beside it, the facts of what the text lists, no more.
        var structured = System.Text.Json.Nodes.JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
        structured.Remove("text");
        Assert.True(structured.ToJsonString().Length <= McpText.Limit, $"structured content without its text {structured.ToJsonString().Length}");
    }

    private static CallToolResult Render(string markdown, IEnumerable<int> components, Func<ComponentSlot, ComponentSlot>? shape = null)
    {
        var page = new PageDto(Guid.NewGuid(), Guid.NewGuid(), null, "Nasazení", 1, true, "{}", markdown, 1, DateTime.UtcNow, null, true);
        var read = new McpPageRead(page, false, 1, "Wiki", "Wiki / Nasazení", new Dictionary<Guid, string>());
        var slots = components.Select(i => new ComponentSlot(i, "requirement", $"Požadavek {i}", false) { Text = new string('x', 1_000) })
            .Select(shape ?? (slot => slot)).ToArray();
        return McpPageOutput.Render(read, new ContentProjection(markdown, [], slots, false, []), null, null);
    }

    [Fact]
    public void Repeated_headings_without_ids_count_only_the_section_that_changed()
    {
        static object Heading() => new { type = "heading", attrs = new { level = 2 },
            content = new[] { new { type = "text", text = "Repeated " }, new { type = "text", text = "heading" } } };
        static object Paragraph(string text) => new { type = "paragraph", content = new[] { new { type = "text", text } } };
        static string Document(string second) => JsonSerializer.Serialize(new { format = "atlas-rich", version = 1,
            document = new { type = "doc", content = new[] { Heading(), Paragraph("First"), Heading(), Paragraph(second) } } });
        var before = Document("Second");
        Assert.Equal(0, McpPublishSummaries.ChangedSections(before, Document("Second")));
        Assert.Equal(1, McpPublishSummaries.ChangedSections(before, Document("Updated second")));
    }
}
