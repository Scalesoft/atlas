using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>What leaves the wiki is an allow-list (GAP1-3): every node type of the host schema has a decided policy for every audience.</summary>
public sealed class OutboundDocumentTests
{
    [Fact]
    public void Every_node_of_the_host_schema_has_an_outbound_policy()
    {
        Assert.All(HostDocumentValidator.Instance.Schema.NodeNames, type => Assert.True(OutboundDocument.Covers(type), type));
    }

    [Theory]
    [InlineData(OutboundAudience.Public)]
    [InlineData(OutboundAudience.ExternalFile)]
    public void A_node_without_a_policy_fails_the_copy(OutboundAudience audience)
    {
        var json = Envelope(new JsonObject { ["type"] = "futureChip", ["attrs"] = new JsonObject { ["secret"] = "x" } });

        Assert.Throws<InvalidDataException>(() => OutboundDocument.ForAudience(json, audience));
    }

    [Fact]
    public void The_text_for_AI_leaves_a_node_without_a_policy_out_instead_of_failing_the_index()
    {
        var json = Envelope(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "Viditelné " }, new JsonObject { ["type"] = "futureChip", ["attrs"] = new JsonObject { ["label"] = "TAJNÉ" } }) });

        Assert.Equal("Viditelné", OutboundDocument.AiText(json).Trim());
    }

    [Fact]
    public void A_file_keeps_what_the_page_shows_without_the_ids_behind_it()
    {
        const string objectId = "0b6b7c1e-5f1a-4c3e-9a51-6d2f0f4e8a11";
        var json = Envelope(
            new JsonObject
            {
                ["type"] = "contactCard",
                ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject
                {
                    ["name"] = "Petr Dvořák", ["role"] = "PM projektu", ["phones"] = new JsonArray("+420 777 123 456"), ["objectId"] = objectId,
                    ["directoryId"] = "ad-42", ["internalUser"] = new JsonObject { ["id"] = "8f0e3c55-2a7b-4d7e-b3c1-91a0c4d2e6f7" },
                    ["username"] = "pdvorak", ["futureField"] = "BUDOUCI"
                }) } }
            },
            new JsonObject { ["type"] = "requirement", ["attrs"] = new JsonObject { ["party"] = "IT KBN", ["partyContactId"] = objectId } });

        var file = OutboundDocument.ForAudience(json, OutboundAudience.ExternalFile);

        Assert.Contains("+420 777 123 456", file, StringComparison.Ordinal);
        Assert.Contains("PM projektu", file, StringComparison.Ordinal);
        Assert.Contains("IT KBN", file, StringComparison.Ordinal);
        // An allow-list of what the card shows: the login and a key added later stay inside.
        foreach (var hidden in new[] { objectId, "ad-42", "8f0e3c55", "internalUser", "pdvorak", "BUDOUCI" })
            Assert.DoesNotContain(hidden, file, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_names_only_pages_its_owner_reads()
    {
        var readable = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        static JsonObject Link(Guid id, string title) => new()
        {
            ["type"] = "pageReference", ["attrs"] = new JsonObject { ["id"] = id.ToString(), ["title"] = title, ["url"] = $"#/pages/{id}" }
        };
        var json = Envelope(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(Link(readable, "Starý název"), Link(hidden, "Propouštění Q4")) });

        var file = OutboundDocument.ForAudience(json, OutboundAudience.ExternalFile, new Dictionary<Guid, string> { [readable] = "Kontakty" });

        Assert.Contains("Kontakty", file, StringComparison.Ordinal);
        Assert.Contains(readable.ToString(), file, StringComparison.Ordinal);
        Assert.Contains("Odkaz na stránku", file, StringComparison.Ordinal);
        foreach (var leaked in new[] { "Starý název", "Propouštění Q4", hidden.ToString() })
            Assert.DoesNotContain(leaked, file, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_for_AI_reads_an_OpenAPI_specification_only_outside_a_concealed_block()
    {
        static JsonObject Api(string name) => new()
        {
            ["type"] = "openapi", ["attrs"] = new JsonObject { ["data"] = new JsonObject
            {
                ["version"] = 1, ["filename"] = name + ".yaml",
                ["source"] = $"openapi: 3.1.0\ninfo:\n  title: {name}\n  version: '1'\npaths:\n  /{name}:\n    get:\n      summary: Operace{name}\n"
            } }
        };
        var json = Envelope(Api("VEREJNE"), new JsonObject { ["type"] = "concealedContent", ["content"] = new JsonArray(Api("SKRYTE")) });

        var text = OutboundDocument.AiText(json);

        Assert.Contains("OperaceVEREJNE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SKRYTE", text, StringComparison.Ordinal);
    }

    private static string Envelope(params JsonNode[] blocks) => new JsonObject
    {
        ["format"] = "atlas-rich", ["version"] = 1, ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks) }
    }.ToJsonString();
}
