using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Atlas.Markdown.Sync;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// Embeds of attachments on a realistic page: images of the page's attachments are Markdown in the projection and come back from it
/// with their stored look when their Markdown is unchanged (at the top level, in a table and in a callout), new ones get the editor's
/// default node, and only canonical attachment addresses become images or download links. The look changes "on the side"
/// (<see cref="RichMarkdownMerge.UpdateEmbed"/>), where every block, component, table attribute, heading id, presentation and plugin
/// declaration outside the touched node stays as stored and every node written uses only the attributes of the editor schema.
/// </summary>
public sealed class RichEmbedTests
{
    private static readonly Guid ImageA = Guid.Parse("6a1f1d0e-21c4-4c55-9e0c-0d1c8f0a0a01");
    private static readonly Guid FileB = Guid.Parse("6a1f1d0e-21c4-4c55-9e0c-0d1c8f0a0b02");
    private static readonly Guid ImageC = Guid.Parse("6a1f1d0e-21c4-4c55-9e0c-0d1c8f0a0c03");
    private static readonly EmbedAttachment NewImage = new(Guid.Parse("6a1f1d0e-21c4-4c55-9e0c-0d1c8f0a0d04"), "nove-schema.png", 4096, "image/png", true);
    private static readonly EmbedAttachment NewFile = new(Guid.Parse("6a1f1d0e-21c4-4c55-9e0c-0d1c8f0a0e05"), "prirucka.pdf", 81920, "application/pdf", false);

    [Trait("Category", "Smoke")]
    [Fact]
    public void Attachment_images_round_trip_as_Markdown_and_keep_their_look_unless_changed()
    {
        var page = Manual();
        var projection = Project(page);
        // Images, the inline contact and the chart are text (a-contact, a-chart); the table with cell colours and the file card stay placeholders.
        Assert.Contains("![Schéma sítě](/api/attachments/" + ImageA + "/content)", projection.Markdown, StringComparison.Ordinal);
        Assert.Contains("> ![Varování](/api/attachments/" + ImageC + "/content)", projection.Markdown, StringComparison.Ordinal);
        Assert.Equal(["table", "attachment"], projection.Components.Select(x => x.Type));

        // The Markdown sent back unchanged is the stored document, presentations and heading ids included.
        var same = RichMarkdownMerge.ReplaceAll(page, projection, projection.Markdown, Context());
        Assert.True(JsonNode.DeepEquals(page.Document, JsonNode.Parse(same.ContentJson)!["document"]), same.ContentJson);

        // Text around the images changes (the callout and the paragraph before image A): unchanged images keep their stored node, also
        // inside the rewritten callout; an image with a new alternative text keeps its size and look, a new image gets the editor's default node.
        var edited = projection.Markdown
            .Replace("Otevřete nastavení.", "Otevřete nastavení sítě.", StringComparison.Ordinal)
            .Replace("> ![Varování](/api/attachments/" + ImageC + "/content)", "> ![Varování](/api/attachments/" + ImageC + "/content)\n>\n> Pozor na výpadek.", StringComparison.Ordinal)
            .Replace("![Schéma sítě]", "![Schéma sítě 2]", StringComparison.Ordinal)
            .Replace("## Závěr", "Viz ![Nové schéma](/api/attachments/" + NewImage.Id + "/content \"Verze 2\") níže.\n\n## Závěr", StringComparison.Ordinal);
        var blocks = Blocks(RichMarkdownMerge.ReplaceAll(page, projection, edited, Context()));
        var callout = blocks.Single(x => x["type"]!.GetValue<string>() == "callout");
        Assert.Equal(page.Blocks[9]["content"]![0]!.ToJsonString(), callout["content"]![0]!.ToJsonString());
        Assert.Equal("Pozor na výpadek.", callout["content"]![1]!["content"]![0]!["text"]!.GetValue<string>());
        var described = (JsonObject)page.Blocks[4].DeepClone();
        described["attrs"]!["alt"] = "Schéma sítě 2";
        Assert.True(JsonNode.DeepEquals(described, blocks[4]), blocks[4].ToJsonString());
        Assert.Equal(page.Blocks[6].ToJsonString(), blocks[6].ToJsonString());
        var added = blocks.Skip(8).Take(3).ToArray();
        Assert.Equal(["paragraph", "image", "paragraph"], added.Select(x => x["type"]!.GetValue<string>()));
        Assert.Equal("Viz", added[0]["content"]![0]!["text"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$$$"""
            {"type":"image","attrs":{"src":"/api/attachments/{{{{NewImage.Id}}}}/content","alt":"Nové schéma","title":"Verze 2","width":null,"height":null,"presentation":null}}
            """), added[1]), added[1].ToJsonString());
        Assert.Equal("níže.", added[2]["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void An_image_replaced_by_another_picture_in_Markdown_is_a_formatting_loss_until_reset()
    {
        var page = Manual();
        var projection = Project(page);
        // Image A (caption, crop, width) becomes a new picture where it stood: its look has no stored image to stay with.
        var replaced = projection.Markdown.Replace("![Schéma sítě](/api/attachments/" + ImageA + "/content)",
            "![Schéma sítě](/api/attachments/" + NewImage.Id + "/content)", StringComparison.Ordinal);
        var refused = Assert.Throws<ApiException>(() => RichMarkdownMerge.ReplaceAll(page, projection, replaced, Context()));
        Assert.Equal((409, "formatting_lost"), (refused.Status, refused.Code));
        Assert.Contains("obrázek (3. blok sekce h-postup: vzhled obrázku)", refused.Message, StringComparison.Ordinal);
        Assert.Null(Blocks(RichMarkdownMerge.ReplaceAll(page, projection, replaced, Context(), resetFormatting: true))[4]["attrs"]!["presentation"]);
        // Removed without a replacement it is simply gone.
        var removed = projection.Markdown.Replace("![Schéma sítě](/api/attachments/" + ImageA + "/content)", "", StringComparison.Ordinal);
        Assert.Equal(page.Blocks.Count - 1, Blocks(RichMarkdownMerge.ReplaceAll(page, projection, removed, Context())).Count);
    }

    [Fact]
    public void Images_in_lists_are_Markdown_and_keep_their_look_while_a_table_cell_image_stays_in_its_placeholder()
    {
        var look = """{"width":40,"align":"left","caption":"","rotate":0,"flip":false,"brightness":100,"contrast":100,"saturation":100,"filter":"none","cropLeft":0,"cropRight":0,"cropTop":0,"cropBottom":0,"naturalWidth":0,"naturalHeight":0}""";
        var page = Document(Plugins(), JsonNode.Parse($$$$"""
            {"type":"bulletList","attrs":{"tight":false},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Bod"}]},
              {"type":"image","attrs":{"src":"/api/attachments/{{{{ImageC}}}}/content","alt":"V seznamu","title":null,"width":null,"height":null,"presentation":{{{{look}}}}}}]}]}
            """)!.AsObject(), JsonNode.Parse($$$$"""
            {"type":"table","content":[
              {"type":"tableRow","content":[{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Krok"}]}]}]},
              {"type":"tableRow","content":[{"type":"tableCell","content":[
                {"type":"image","attrs":{"src":"/api/attachments/{{{{ImageA}}}}/content","alt":"V tabulce","title":null,"width":null,"height":null,"presentation":null}}]}]}]}
            """)!.AsObject(), Paragraph());
        var projection = Project(page);
        // The editor writes a GFM table only for cells of one paragraph: a cell with an image keeps the table a placeholder.
        Assert.Equal("table", Assert.Single(projection.Components).Type);
        Assert.Equal(new ComponentEmbed(ImageA, EmbedKind.Image, 1), Assert.Single(Assert.Single(projection.Components).Embeds));

        var blocks = Blocks(RichMarkdownMerge.ReplaceAll(page, projection, projection.Markdown.Replace("Bod", "První bod", StringComparison.Ordinal), Context()));
        Assert.Equal("První bod", blocks[0]["content"]![0]!["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(look, blocks[0]["content"]![0]!["content"]![1]!["attrs"]!["presentation"]!.ToJsonString());
        Assert.Equal(page.Blocks[1].ToJsonString(), blocks[1].ToJsonString());
    }

    [Fact]
    public void Many_images_of_one_attachment_with_new_alternative_texts_keep_their_looks_in_bounded_time()
    {
        // A quote of images of one attachment, each first repeated with a new alternative text: the new ones take the stored images
        // by order (whose own Markdown follows later), the repeats get the default node. The work must stay linear in the images
        // (it was quadratic: seconds for a few thousand).
        const int count = 4000;
        var quote = new JsonObject
        {
            ["type"] = "blockquote",
            ["content"] = new JsonArray([.. Enumerable.Range(0, count).Select(x => JsonNode.Parse($$$$"""
                {"type":"image","attrs":{"src":"/api/attachments/{{{{ImageA}}}}/content","alt":"a{{{{x}}}}","title":null,"width":{{{{x + 1}}}},"height":null,"presentation":null}}
                """))]),
        };
        var page = Document(Plugins(), quote, Paragraph());
        var projection = Project(page);
        Assert.Empty(projection.Components);
        var stored = projection.Markdown.TrimEnd('\n');
        var edited = stored.Replace("![a", "![b", StringComparison.Ordinal) + "\n>\n" + stored + "\n";

        JsonArray images = [];
        var spent = ThreadCpuTime.Of(() => images = Blocks(RichMarkdownMerge.ReplaceAll(page, projection, edited, Context()))[0]["content"]!.AsArray());

        Assert.Equal(["b0", "1", "b3999", "4000", "a0", "null"], new[] { images[0]!, images[count - 1]!, images[count]! }
            .SelectMany(x => new[] { x["attrs"]!["alt"]!.GetValue<string>(), x["attrs"]!["width"]?.ToJsonString() ?? "null" }));
        // This thread's processor time, not the wall clock a busy machine stretches (V6); the linear merge takes well under a second.
        Assert.True(spent < TimeSpan.FromSeconds(5), spent.ToString());
    }

    [Fact]
    public void Only_attachment_addresses_become_images_and_download_links()
    {
        var upper = ImageA.ToString().ToUpperInvariant();
        var parsed = MarkdownToRich.Parse($$"""
            ![Vzdálený](https://example.com/a.png)

            ![Data](data:image/png;base64,AAAA)

            [Smlouva](/api/attachments/{{upper}}/download) a [obsah](/api/attachments/{{ImageA}}/content)

            ## Nadpis ![Schéma](/api/attachments/{{ImageA}}/content)
            """);
        var json = parsed.Blocks.ToJsonString();
        Assert.DoesNotContain("\"image\"", json, StringComparison.Ordinal);
        // A remote image stays a link to its address; any other address is text.
        Assert.Contains("\"href\":\"https://example.com/a.png\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", json, StringComparison.Ordinal);
        // A download link is stored canonical; the inline content address is no link.
        Assert.Contains("\"href\":\"/api/attachments/" + ImageA + "/download\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/content", json, StringComparison.Ordinal);
        Assert.Equal(3, parsed.Warnings.Count);

        // A stored remote image is never made from Markdown, so it stays a placeholder.
        var remote = Document(Plugins(), Paragraph(Text("Text.")), new JsonObject
        {
            ["type"] = "image", ["attrs"] = new JsonObject { ["src"] = "https://example.com/a.png", ["alt"] = "Vzdálený" },
        }, Paragraph());
        Assert.Equal("image", Assert.Single(Project(remote).Components).Type);
    }

    [Fact]
    public void Replacing_or_restyling_an_embed_changes_only_that_node()
    {
        var page = Manual();
        // Image A is shown twice (below "Postup" and in the table): which one must be said.
        Assert.Equal((400, "embed_ambiguous"), Code(() => RichMarkdownMerge.UpdateEmbed(page, ImageA, null, new ImageLook(Alt: "X"), null)));
        Assert.Equal((404, "embed_not_found"), Code(() => RichMarkdownMerge.UpdateEmbed(page, ImageA, 3, new ImageLook(Alt: "X"), null)));
        Assert.Equal((404, "embed_not_found"), Code(() => RichMarkdownMerge.UpdateEmbed(page, NewImage.Id, null, new ImageLook(Alt: "X"), null)));

        // The second A (in the table cell) replaced by a new image: the source changes, the alt and the stored look stay.
        var replaced = RichMarkdownMerge.UpdateEmbed(page, ImageA, 2, new ImageLook(), NewImage);
        var cell = Blocks(replaced)[6]["content"]![0]!["content"]![1]!["content"]![1]!.AsObject();
        Assert.Equal("/api/attachments/" + NewImage.Id + "/content", cell["attrs"]!["src"]!.GetValue<string>());
        Assert.Equal("Schéma v tabulce", cell["attrs"]!["alt"]!.GetValue<string>());
        AssertOnlyTouched(page, replaced, [6, 0, 1, 1]);

        // The first A gets a new width and a new picture: the rest of its presentation stays, the old picture's size is forgotten.
        var restyled = RichMarkdownMerge.UpdateEmbed(page, ImageA, 1, new ImageLook(Width: 40), NewImage);
        var presentation = Blocks(restyled)[4]["attrs"]!["presentation"]!;
        Assert.Equal(40, presentation["width"]!.GetValue<int>());
        Assert.Equal("left", presentation["align"]!.GetValue<string>());
        Assert.Equal("Obrázek 1: schéma sítě", presentation["caption"]!.GetValue<string>());
        Assert.Equal(10, presentation["cropTop"]!.GetValue<int>());
        Assert.Equal(0, presentation["naturalWidth"]!.GetValue<int>());
        AssertOnlyTouched(page, restyled, [4]);

        // A card takes another file and keeps its description; an image must stay an image and a card has no look.
        var card = RichMarkdownMerge.UpdateEmbed(page, FileB, null, new ImageLook(), NewFile);
        Assert.Equal("Podepsaná verze", Blocks(card)[7]["attrs"]!["data"]!["description"]!.GetValue<string>());
        Assert.Equal("prirucka.pdf", Blocks(card)[7]["attrs"]!["data"]!["name"]!.GetValue<string>());
        AssertOnlyTouched(page, card, [7]);
        Assert.Equal((400, "attachment_not_image"), Code(() => RichMarkdownMerge.UpdateEmbed(page, ImageC, null, new ImageLook(), NewFile)));
        Assert.Equal((400, "invalid_argument"), Code(() => RichMarkdownMerge.UpdateEmbed(page, FileB, null, new ImageLook(Width: 50), null)));
    }

    [Fact]
    public void A_stored_node_the_editor_would_refuse_is_not_touched()
    {
        // A node hand-built by a migration client with an attribute the editor does not know (the production failure): changing it
        // would write it again, so the guard refuses and nothing is saved.
        var foreign = new JsonObject
        {
            ["type"] = "image",
            ["attrs"] = new JsonObject { ["src"] = "/api/attachments/" + ImageA + "/content", ["alt"] = "Staré", ["caption"] = "nepodporované" },
        };
        var page = Document(Plugins(), Heading(1, "h-a", "A"), foreign, Paragraph());
        Assert.Equal((409, "embed_invariant_failed"), Code(() => RichMarkdownMerge.UpdateEmbed(page, ImageA, null, new ImageLook(Alt: "Nové"), null)));
        // A stored presentation with a key the editor does not know is refused too, not rebuilt without it.
        var tinted = new JsonObject
        {
            ["type"] = "image",
            ["attrs"] = new JsonObject
            {
                ["src"] = "/api/attachments/" + ImageA + "/content",
                ["presentation"] = new JsonObject { ["width"] = 50, ["tint"] = "red" },
            },
        };
        var withTint = Document(Plugins(), Heading(1, "h-a", "A"), tinted, Paragraph());
        Assert.Equal((409, "embed_invariant_failed"), Code(() => RichMarkdownMerge.UpdateEmbed(withTint, ImageA, null, new ImageLook(Width: 40), null)));
        // An image look outside the editor's ranges never gets that far.
        Assert.Equal((400, "invalid_argument"), Code(() => RichMarkdownMerge.UpdateEmbed(page, ImageA, null, new ImageLook(Width: 120), null)));
        Assert.Equal((400, "invalid_argument"), Code(() => RichMarkdownMerge.UpdateEmbed(page, ImageA, null, new ImageLook(Align: "justify"), null)));
    }

    [Fact]
    public void Every_written_node_uses_only_the_editor_schema()
    {
        // The editor package's validation, not a hand-copied attribute list.
        var page = Manual();
        foreach (var written in new[]
        {
            RichMarkdownMerge.UpdateEmbed(page, ImageC, null, new ImageLook("A", 30, "center"), null),
            RichMarkdownMerge.UpdateEmbed(page, ImageA, 1, new ImageLook("B", 70, "left"), NewImage),
            RichMarkdownMerge.UpdateEmbed(page, FileB, null, new ImageLook(), NewFile),
        })
        {
            Assert.Null(MarkdownDocumentValidator.Builtin.Validate(written.ContentJson));
        }
    }

    [Fact]
    public void The_projection_names_each_embed_in_a_placeholder_with_its_occurrence_on_the_page()
    {
        // Image A is text, then in the table placeholder; a card of the same attachment after them is its third occurrence.
        var page = Manual();
        var second = Document(Plugins(), [.. page.Blocks.Select(x => (JsonObject)x.DeepClone()), Module("attachment", new JsonObject
        {
            ["version"] = 1, ["name"] = "schema.png", ["url"] = "/api/attachments/" + ImageA + "/download",
        })]);
        var embeds = Project(second).Components.SelectMany(x => x.Embeds.Select(e => (x.Type, e.AttachmentId, e.Kind, e.Occurrence))).ToList();
        Assert.Equal(new[] { ("table", ImageA, EmbedKind.Image, 2), ("attachment", FileB, EmbedKind.File, 1), ("attachment", ImageA, EmbedKind.File, 3) }, embeds);
    }

    // ---- the page ----

    /// <summary>
    /// A manual as the editor stores it: headings with ids and a subsection, marks, an inline contact, an image with a full
    /// presentation, a table with cell attributes holding text and the same image again, a file card with a description, a callout
    /// holding only an image, a chart and the spacing paragraph.
    /// </summary>
    private static RichDocument Manual()
    {
        var table = JsonNode.Parse($$$$"""
            {"type":"table","content":[{"type":"tableRow","attrs":{"height":null},"content":[
              {"type":"tableHeader","attrs":{"colspan":1,"rowspan":1,"colwidth":[180],"backgroundColor":"amber","textAlign":"center"},
               "content":[{"type":"paragraph","content":[{"type":"text","text":"Krok"}]}]},
              {"type":"tableCell","attrs":{"colspan":2,"rowspan":1,"colwidth":null,"backgroundColor":null,"textAlign":null},
               "content":[{"type":"paragraph","content":[{"type":"text","marks":[{"type":"bold"}],"text":"Nastavení"}]},
                          {"type":"image","attrs":{"src":"/api/attachments/{{{{ImageA}}}}/content","alt":"Schéma v tabulce","title":null,"width":null,"height":null,"presentation":null}}]}]}]}
            """)!.AsObject();
        var callout = JsonNode.Parse($$$$"""
            {"type":"callout","attrs":{"variant":"warning"},"content":[
              {"type":"image","attrs":{"src":"/api/attachments/{{{{ImageC}}}}/content","alt":"Varování","title":null,"width":null,"height":null,
               "presentation":{"width":30,"align":"right","caption":"","rotate":0,"flip":false,"brightness":100,"contrast":100,"saturation":100,
               "filter":"none","cropLeft":0,"cropRight":0,"cropTop":0,"cropBottom":0,"naturalWidth":0,"naturalHeight":0}}}]}
            """)!.AsObject();
        return Document(Plugins(),
            Heading(1, "h-uvod", "Úvod"),
            Paragraph(Text("Příručka pro "), new JsonObject { ["type"] = "text", ["marks"] = new JsonArray(new JsonObject { ["type"] = "italic" }), ["text"] = "správce" },
                Text(", kontakt "), Module("contactInline", new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = "Jan Novák" }) })),
            Heading(2, "h-postup", "Postup"),
            Paragraph(Text("Otevřete nastavení.")),
            JsonNode.Parse($$$$"""
                {"type":"image","attrs":{"src":"/api/attachments/{{{{ImageA}}}}/content","alt":"Schéma sítě","title":null,"width":null,"height":null,
                 "presentation":{"width":60,"align":"left","caption":"Obrázek 1: schéma sítě","rotate":0,"flip":false,"brightness":110,"contrast":100,
                 "saturation":100,"filter":"none","cropLeft":0,"cropRight":0,"cropTop":10,"cropBottom":0,"naturalWidth":1600,"naturalHeight":900}}}
                """)!.AsObject(),
            Heading(3, "h-detail", "Detail"),
            table,
            Module("attachment", new JsonObject
            {
                ["version"] = 1, ["name"] = "smlouva.pdf", ["url"] = "/api/attachments/" + FileB + "/download", ["size"] = 2048,
                ["mimeType"] = "application/pdf", ["description"] = "Podepsaná verze",
            }),
            Heading(2, "h-zaver", "Závěr"),
            callout,
            Module("chart", new JsonObject
            {
                ["version"] = 1, ["title"] = "Tržby", ["kind"] = "bar", ["grid"] = new JsonArray(new JsonArray("Měsíc", "Tržby"), new JsonArray("Leden", "12")),
            }),
            Paragraph());
    }

    private static JsonArray Plugins()
    {
        return new JsonArray(
            new JsonObject { ["id"] = "attachments", ["version"] = 1 }, new JsonObject { ["id"] = "charts", ["version"] = 1 },
            new JsonObject { ["id"] = "contacts", ["version"] = 2 }, new JsonObject { ["id"] = "heading-anchors", ["version"] = 1 });
    }

    // ---- helpers ----

    /// <summary>
    /// The invariant, checked independently of the seam: with the touched node at <paramref name="path"/> replaced by one marker on
    /// both sides, the blocks are equal but for a spacing paragraph closing the page, and so is the envelope.
    /// </summary>
    private static void AssertOnlyTouched(RichDocument page, MergeResult result, int[] path)
    {
        var stored = new JsonArray(page.Blocks.Select(x => x.DeepClone()).ToArray());
        var written = JsonNode.Parse(result.ContentJson)!.AsObject();
        var blocks = written["document"]!["content"]!.DeepClone().AsArray();
        Mark(stored, path);
        Mark(blocks, path);
        if (blocks.Count == stored.Count + 1 && blocks[^1]!.ToJsonString() == """{"type":"paragraph"}""")
        {
            blocks.RemoveAt(blocks.Count - 1);
        }
        Assert.Equal(stored.ToJsonString(), blocks.ToJsonString());
        var envelope = JsonNode.Parse(page.Document.Root.ToJsonString())!.AsObject();
        Assert.Equal(envelope["format"]!.ToJsonString(), written["format"]!.ToJsonString());
        Assert.Equal(envelope["version"]!.ToJsonString(), written["version"]!.ToJsonString());
        Assert.Equal((envelope["plugins"] ?? new JsonArray()).ToJsonString(), (written["plugins"] ?? new JsonArray()).ToJsonString());
    }

    private static void Mark(JsonArray root, int[] path)
    {
        var parent = root;
        foreach (var index in path[..^1])
        {
            parent = parent[index]!["content"]!.AsArray();
        }
        parent[path[^1]] = new JsonObject { ["type"] = "touched" };
    }

    private static MergeContext Context()
    {
        var ids = 0;
        return new MergeContext(_ => PageLinkResolution.None, _ => false, () => "h-new-" + ++ids);
    }

    private static RichDocument Document(JsonArray plugins, params JsonObject[] blocks)
    {
        var envelope = new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["plugins"] = plugins.DeepClone(),
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(x => x.DeepClone()).ToArray()) },
        };
        return RichDocument.Parse(envelope.ToJsonString());
    }

    private static ContentProjection Project(RichDocument document)
    {
        return RichMarkdownProjection.Project(document, new ProjectionOptions(1));
    }

    private static IReadOnlyList<JsonObject> Blocks(MergeResult result)
    {
        return RichDocument.Parse(result.ContentJson).Blocks;
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

    private static JsonObject Heading(int level, string id, string text)
    {
        return new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject { ["level"] = level, ["headingId"] = id },
            ["content"] = new JsonArray(Text(text)),
        };
    }

    private static JsonObject Module(string type, JsonObject data)
    {
        return new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["data"] = data } };
    }
}
