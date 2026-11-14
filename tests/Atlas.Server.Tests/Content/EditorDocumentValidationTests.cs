using System.Text.Json.Nodes;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Validation;
using Atlas.Markdown.Sync;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// What Atlas adds to the editor package's document validation (Atlas.Markdown.Sync's <see cref="MarkdownDocumentValidator"/>,
/// whose rules and verdict corpus the editor repository owns): the plugin set Atlas binds, the documents Atlas itself writes, and
/// the 400 <c>invalid_document</c> contract of a write.
/// </summary>
public sealed class EditorDocumentValidationTests
{
    [Fact]
    public void The_validator_covers_exactly_the_plugins_Atlas_binds()
    {
        Assert.Equal(CollaborationSchema.CurrentPluginVersions.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value)),
            HostDocumentValidator.Instance.Schema.Plugins.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => (x.Id, x.Version)));
    }

    /// <summary>Projector's requirement blocks: the web's own plugin (atlas-requirements), outside the editor package's manifest.</summary>
    [Fact]
    public void A_write_accepts_requirement_blocks_and_refuses_a_state_outside_the_contract()
    {
        var page = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirements.json")))![0]!["content"]!;
        Assert.Empty(Rules.ValidateDocument(page.ToJsonString(), ""));
        page["document"]!["content"]![2]!["attrs"]!["state"] = "foo";
        var error = Assert.Throws<ApiException>(() => Rules.ValidateDocument(page.ToJsonString(), ""));
        Assert.Equal((400, "invalid_document", "document.content[2].attrs.state"), (error.Status, error.Code, error.Extensions!["path"]));
    }

    /// <summary>What the editor refuses too (src/projector/requirements.test.ts): a block that does not start with its title, a party over two lines, a key with capitals.</summary>
    [Theory]
    [InlineData("""{"type":"requirement","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Název"}]}]}""", "document.content[0].content")]
    [InlineData("""{"type":"requirement","attrs":{"party":"IT\nKBN"},"content":[{"type":"paragraph"}]}""", "document.content[0].attrs.party")]
    [InlineData("""{"type":"requirement","attrs":{"key":"Velká písmena"},"content":[{"type":"paragraph"}]}""", "document.content[0].attrs.key")]
    public void A_requirement_block_outside_its_contract_is_refused(string block, string path)
    {
        var envelope = new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["plugins"] = new JsonArray(new JsonObject { ["id"] = "atlas-requirements", ["version"] = 1 }),
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(JsonNode.Parse(block)) },
        }.ToJsonString();
        var error = Assert.Throws<ApiException>(() => Rules.ValidateDocument(envelope, ""));
        Assert.Equal((400, "invalid_document", path), (error.Status, error.Code, error.Extensions!["path"]));
    }

    /// <summary>The documents the server itself writes (Markdown conversion, wiki import, MCP merge).</summary>
    [Theory]
    [InlineData("content", "components.json")]
    [InlineData("content", "merge.json")]
    [InlineData("content", "text-nodes.json")]
    [InlineData("content", "mark-order.json")]
    [InlineData("content", "requirements.json")]
    [InlineData("wiki-import", "github.json")]
    [InlineData("wiki-import", "obsidian.json")]
    [InlineData("wiki-import", "wikijs.json")]
    [InlineData("wiki-import", "constructs.json")]
    public void Accepts_the_documents_the_server_writes(string set, string file)
    {
        foreach (var page in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", set, "expected", file)))!.AsArray())
        {
            var violation = HostDocumentValidator.Instance.Validate(page!["content"]!.ToJsonString());
            Assert.True(violation is null, $"{page["path"] ?? page["name"]}: {violation}");
        }
    }

    [Theory]
    // The production incident: an attribute the image node does not have.
    [InlineData("""{"type":"image","attrs":{"src":"/api/attachments/0f8fad5b-d9cb-469f-a165-70867728950e/content","align":"center"}}""", "document.content[0].attrs.align")]
    public void A_write_answers_invalid_document_naming_the_path(string block, string path)
    {
        var envelope = new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(JsonNode.Parse(block)) },
        }.ToJsonString();
        var error = Assert.Throws<ApiException>(() => Rules.ValidateDocument(envelope, ""));
        Assert.Equal((400, "invalid_document", path), (error.Status, error.Code, error.Extensions!["path"]));
        Assert.StartsWith($"Obsah editoru neodpovídá schématu editoru Atlasu: {path} není atribut tohoto uzlu nebo značky (", error.Message, StringComparison.Ordinal);
    }
}
