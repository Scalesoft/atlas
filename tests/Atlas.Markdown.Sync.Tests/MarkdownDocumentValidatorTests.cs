using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>The public validator API: verdicts with stable codes and paths, bounds, narrowed plugin sets and host extensions.</summary>
public sealed class MarkdownDocumentValidatorTests
{
    private static string Envelope(string content, string plugins = "[]")
        => "{\"format\":\"atlas-rich\",\"version\":1,\"plugins\":" + plugins + ",\"document\":{\"type\":\"doc\",\"content\":[" + content + "]}}";

    [Theory]
    [InlineData("not json", "$", MarkdownDocumentViolationCodes.InvalidJson)]
    [InlineData("""{"format":"markdown","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""", "format", MarkdownDocumentViolationCodes.InvalidEnvelope)]
    [InlineData("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":"justify"}}]}}""",
        "document.content[0].attrs.textAlign", MarkdownDocumentViolationCodes.InvalidAttribute)]
    [InlineData("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"mathBlock"}]}}""",
        "document.content[0]", MarkdownDocumentViolationCodes.MissingPlugin)]
    [InlineData("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","type":"heading"}]}}""",
        "document.content[0].type", MarkdownDocumentViolationCodes.DuplicateField)]
    [InlineData("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link","attrs":{"href":"javascript:x"}}]}]}]}}""",
        "document.content[0].content[0].marks[0].attrs.href", MarkdownDocumentViolationCodes.InvalidAttribute)]
    [InlineData("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"callout"}]}}""",
        "document.content[0].content", MarkdownDocumentViolationCodes.InvalidContent)]
    [InlineData("""{"format":"atlas-rich","version":1,"plugins":[{"id":"secrets","version":9}],"document":{"type":"doc","content":[{"type":"paragraph"}]}}""",
        "plugins[0].version", MarkdownDocumentViolationCodes.UnsupportedPluginVersion)]
    public void Reports_the_first_violation_with_a_stable_code_and_path(string json, string path, string code)
    {
        var violation = MarkdownDocumentValidator.Builtin.Validate(json);
        Assert.NotNull(violation);
        Assert.Equal((path, code), (violation.Path, violation.Code));
        Assert.False(string.IsNullOrWhiteSpace(violation.Message));
    }

    [Fact]
    public void A_contacts_unresolved_flag_is_a_boolean()
    {
        string Inline(string flag) => Envelope("""{"type":"paragraph","content":[{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Jana","unresolved":"""
            + flag + "}]}}}]}", """[{"id":"contacts","version":2}]""");
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(Inline("true")));
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(Inline("null")));
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(Inline("\"true\"")));
        Assert.Equal(MarkdownDocumentViolationCodes.InvalidAttribute, MarkdownDocumentValidator.Builtin.Validate(Inline("\"yes\""))?.Code);
        Assert.Equal(MarkdownDocumentViolationCodes.InvalidAttribute, MarkdownDocumentValidator.Builtin.Validate(Inline("1"))?.Code);
    }

    [Fact]
    public void Accepts_a_migratable_plugin_version_and_reads_bytes_and_elements_alike()
    {
        var json = Envelope("""{"type":"secretReference","attrs":{"secretId":"5b2f6a3c-1d4e-4f5a-8b6c-7d8e9f0a1b2c"}}""", """[{"id":"secrets","version":2}]""");
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(json));
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(System.Text.Encoding.UTF8.GetBytes(json)));
        using var document = JsonDocument.Parse(json);
        Assert.Null(MarkdownDocumentValidator.Builtin.Validate(document.RootElement));
    }

    [Fact]
    public void Deep_or_huge_input_yields_a_verdict_without_throwing()
    {
        var deep = new string('[', 10_000) + new string(']', 10_000);
        Assert.Equal(MarkdownDocumentViolationCodes.InvalidJson, MarkdownDocumentValidator.Builtin.Validate(deep)!.Code);
        JsonNode node = new JsonObject { ["type"] = "paragraph" };
        for (var level = 0; level < 64; level++) node = new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(node) };
        Assert.Equal(MarkdownDocumentViolationCodes.TooDeep, MarkdownDocumentValidator.Builtin.Validate(Envelope(node.ToJsonString()))!.Code);
        var rows = string.Join(',', Enumerable.Repeat("""{"type":"tableRow","content":[{"type":"tableCell","attrs":{"colspan":1000},"content":[{"type":"paragraph"}]}]}""", 2));
        Assert.Equal(MarkdownDocumentViolationCodes.TableTooLarge,
            MarkdownDocumentValidator.Builtin.Validate(Envelope("{\"type\":\"table\",\"content\":[" + rows + "]}"))!.Code);
    }

    [Fact]
    public void A_narrowed_plugin_set_refuses_the_content_of_the_other_plugins()
    {
        var mathOnly = new MarkdownDocumentValidator(MarkdownSchemaManifest.Builtin.WithPlugins(["math"]));
        Assert.Equal(["math"], mathOnly.Schema.Plugins.Keys.ToArray());
        Assert.DoesNotContain("chart", mathOnly.Schema.NodeNames);
        Assert.DoesNotContain("headingId", mathOnly.Schema.AttributesOf("heading"));
        Assert.Null(mathOnly.Validate(Envelope("""{"type":"mathBlock"}""", """[{"id":"math","version":1}]""")));
        Assert.Equal(MarkdownDocumentViolationCodes.UnknownNode, mathOnly.Validate(Envelope("""{"type":"chart"}"""))!.Code);
        Assert.Equal(MarkdownDocumentViolationCodes.UnknownAttribute,
            mathOnly.Validate(Envelope("""{"type":"heading","attrs":{"level":1,"headingId":"a"}}"""))!.Code);
        Assert.Equal(MarkdownDocumentViolationCodes.UnknownPlugin,
            mathOnly.Validate(Envelope("""{"type":"paragraph"}""", """[{"id":"charts","version":1}]"""))!.Code);
        Assert.Throws<ArgumentException>(() => MarkdownSchemaManifest.Builtin.WithPlugins(["no-such-plugin"]));
    }

    [Fact]
    public void A_host_manifest_with_its_own_structured_validator_and_image_policy()
    {
        using var stream = typeof(MarkdownSchemaManifest).Assembly.GetManifestResourceStream(MarkdownSchemaManifest.BuiltinResource)!;
        var manifest = JsonNode.Parse(stream)!;
        var image = manifest["nodes"]!.AsArray().Single(node => (string)node!["name"]! == "image")!;
        image["attrs"]!.AsArray().Single(attribute => (string)attribute!["name"]! == "src")!["constraint"] =
            JsonNode.Parse("""{"kind":"url","policy":"image","nullable":true,"imageSources":{"allowDataImages":false,"allowedOrigins":["https://cdn.example.com"]}}""");
        var math = manifest["nodes"]!.AsArray().Single(node => (string)node!["name"]! == "mathBlock")!;
        math["attrs"]![0]!["constraint"] = JsonNode.Parse("""{"kind":"structured","validator":"host:math"}""");
        var schema = MarkdownSchemaManifest.Parse(manifest.ToJsonString());
        Assert.Throws<ArgumentException>(() => new MarkdownDocumentValidator(schema));
        var validator = new MarkdownDocumentValidator(schema, new Dictionary<string, MarkdownValueValidator>
        {
            ["host:math"] = (value, _) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("expression", out _) ? null : "needs an expression",
        });
        Assert.Null(validator.Validate(Envelope("""{"type":"image","attrs":{"src":"https://cdn.example.com/a.png"}}""")));
        Assert.Null(validator.Validate(Envelope("""{"type":"image","attrs":{"src":"/relative.png"}}""")));
        Assert.Equal(MarkdownDocumentViolationCodes.InvalidAttribute, validator.Validate(Envelope("""{"type":"image","attrs":{"src":"https://other.example.com/a.png"}}"""))!.Code);
        Assert.Equal(MarkdownDocumentViolationCodes.InvalidAttribute, validator.Validate(Envelope("""{"type":"image","attrs":{"src":"data:image/png;base64,AAAA"}}"""))!.Code);
        Assert.Equal("needs an expression", validator.Validate(Envelope("""{"type":"mathBlock","attrs":{"data":{}}}""", """[{"id":"math","version":1}]"""))!.Message);
    }

    [Fact]
    public void The_collaboration_policy_is_derived_from_the_same_manifest()
    {
        var policy = YjsContentPolicy.CreateDefault();
        Assert.Same(MarkdownDocumentValidator.Builtin, policy.Validator);
        Assert.Equal(["secretInlineReference", "secretReference"], policy.ReservedNodes.Order(StringComparer.Ordinal));
        var narrowed = YjsContentPolicy.FromValidator(new MarkdownDocumentValidator(MarkdownSchemaManifest.Builtin.WithPlugins(["math"])));
        Assert.Empty(narrowed.ReservedNodes);
    }
}
