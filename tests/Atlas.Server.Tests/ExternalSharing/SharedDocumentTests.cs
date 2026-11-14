using System.Text.Json.Nodes;
using Atlas.Server.Features.ExternalSharing;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class SharedDocumentTests
{
    [Theory]
    [InlineData("interactive", "reference", false)]
    [InlineData(null, "reference", false)]
    [InlineData("reference", "reference", false)]
    [InlineData("download", "download", false)]
    [InlineData("future", "reference", false)]
    [InlineData("interactive", "reference", true)]
    public void OpenAPI_blocks_become_documentation_only_without_uploader_and_keep_the_source(string? mode, string expected, bool nested)
    {
        var document = JsonNode.Parse(OpenApiDocuments.Rich(mode: mode))!;
        var content = document["document"]!["content"]!.AsArray();
        if (nested)
        {
            var block = content[1]!;
            content.RemoveAt(1);
            content.Add(new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(block) });
        }
        // Other nodes keep look-alike attributes.
        content.Add(new JsonObject { ["type"] = "paragraph", ["attrs"] = new JsonObject { ["mode"] = "interactive", ["uploadedBy"] = "kept" } });
        var shared = SharedDocument.ForPublic(document.ToJsonString(), new Dictionary<Guid, string>());

        var data = (nested ? content[1]!["content"]![0]! : content[1]!)["attrs"]!["data"]!.AsObject();
        data.Remove("uploadedBy");
        data["mode"] = expected;
        Assert.True(JsonNode.DeepEquals(document, JsonNode.Parse(shared)));
    }

    [Fact]
    public void Requirement_blocks_keep_their_text_and_state_but_not_who_or_where()
    {
        var content = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirements.json")))![0]!["content"]!;
        var shared = SharedDocument.ForPublic(content.ToJsonString(), new Dictionary<Guid, string>());

        Assert.DoesNotContain("IT KBN", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Keepass", shared, StringComparison.Ordinal);
        var markdown = Atlas.Server.Platform.Content.EditorMarkdownWriter.Write(JsonNode.Parse(shared)!["document"]!.AsObject());
        Assert.Contains("- [x] **SSL certifikát** — hotovo 10. 9. 2026 · vstup · klíč: portal.ssl\n  Poznámka pod požadavkem…", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Contacts_and_requirement_parties_leave_only_their_names()
    {
        const string objectId = "0b6b7c1e-5f1a-4c3e-9a51-6d2f0f4e8a11";
        const string userId = "8f0e3c55-2a7b-4d7e-b3c1-91a0c4d2e6f7";
        const string otherPage = "5d1c2b3a-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
        var inline = new JsonObject
        {
            ["type"] = "contactInline",
            ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject
            {
                ["name"] = "Jana Nováková", ["kind"] = "internal", ["username"] = "jnovakova", ["emails"] = new JsonArray("jana@firma.example.test"),
                ["objectId"] = objectId, ["directoryId"] = "ad-42", ["internalUser"] = new JsonObject { ["id"] = userId, ["name"] = "Jana Nováková" }
            }) } }
        };
        var card = new JsonObject
        {
            ["type"] = "contactCard",
            ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject
            {
                ["name"] = "Petr Dvořák", ["phones"] = new JsonArray("+420 777 123 456"), ["address"] = "Dlouhá 1, Praha", ["photo"] = "https://cdn/p.png",
                ["website"] = "https://petr.example", ["role"] = "PM projektu"
            }) } }
        };
        var requirement = new JsonObject
        {
            ["type"] = "requirement",
            ["attrs"] = new JsonObject
            {
                ["party"] = "Petr Dvořák", ["where"] = "Keepass", ["partyContactId"] = objectId, ["requires"] = $"portal.dns,vpn.pristup@{otherPage}"
            }
        };
        var document = new JsonObject
        {
            ["format"] = "atlas-rich", ["version"] = 1,
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(
                new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(inline) }, card, requirement) }
        };
        var shared = SharedDocument.ForPublic(document.ToJsonString(), new Dictionary<Guid, string>());

        Assert.Contains("Jana Nováková", shared, StringComparison.Ordinal);
        Assert.Contains("Petr Dvořák", shared, StringComparison.Ordinal);
        // A dependency on this page stays; one on another page would name that page and its requirement.
        Assert.Contains("portal.dns", shared, StringComparison.Ordinal);
        foreach (var hidden in new[] { "jnovakova", "jana@firma.example.test", objectId, userId, "ad-42", "777 123", "Dlouhá", "cdn/p.png", "petr.example", "PM projektu", "Keepass",
            otherPage, "vpn.pristup" })
            Assert.DoesNotContain(hidden, shared, StringComparison.Ordinal);
    }
}
