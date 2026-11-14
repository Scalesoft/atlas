using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>The stored form of an editor document (<see cref="RichDocument.WithoutDefaultAttributes(string)"/>): what the editor's save
/// writes at a default is left out, as the web editor leaves it out (tests/fixtures/default-attributes.json, storedDocument.test.ts),
/// and the document reads (validation, Markdown) exactly as before.</summary>
public sealed class RichDocumentDefaultsTests
{
    private static readonly JsonObject Fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "default-attributes.json")))!.AsObject();

    private static string Envelope(string part)
    {
        var plugins = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "host-document-schema.json")))!["plugins"]!;
        return new JsonObject { ["format"] = "atlas-rich", ["version"] = 1, ["document"] = Fixture[part]!.DeepClone(), ["plugins"] = plugins.DeepClone() }.ToJsonString();
    }

    [Fact]
    public void Attributes_at_their_default_are_left_out_as_the_editor_leaves_them_out()
    {
        var saved = Envelope("saved");
        var stored = RichDocument.WithoutDefaultAttributes(saved);

        Assert.True(JsonNode.DeepEquals(Fixture["stored"], JsonNode.Parse(stored)!["document"]), JsonNode.Parse(stored)!["document"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(Fixture["stored"], RichDocument.DocumentWithoutDefaults(saved)));
        Assert.Equal(Rules.ValidateDocument(saved).Markdown, Rules.ValidateDocument(stored).Markdown);
        Assert.Equal(stored, RichDocument.WithoutDefaultAttributes(stored));
    }

    /// <summary>A document stored before 5.0 carries the legacy format id <c>scalesoft-rich</c> and reads exactly as the current one.</summary>
    [Fact]
    public void A_document_with_the_legacy_format_id_still_reads()
    {
        var current = Envelope("saved");
        var legacy = current.Replace("\"format\":\"atlas-rich\"", "\"format\":\"scalesoft-rich\"", StringComparison.Ordinal);

        Assert.NotEqual(current, legacy);
        Assert.Equal(RichDocument.Parse(current).Blocks.Count, RichDocument.Parse(legacy).Blocks.Count);
        Assert.Equal(Rules.ValidateDocument(current).Markdown, Rules.ValidateDocument(legacy).Markdown);
    }
}
