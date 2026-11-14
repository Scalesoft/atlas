using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atlas.Server.Tests;

/// <summary>
/// An OpenAPI block as @atlas/markdown persists it (plugins/openapi/data.ts): node type <c>openapi</c>,
/// <c>attrs.data</c> = { version, filename, source, uploadedBy?, mode? }. The server never parses the source.
/// </summary>
internal static class OpenApiDocuments
{
    /// <summary>MAX_OPENAPI_SOURCE_BYTES in the editor.</summary>
    public const int MaxSourceBytes = 512 * 1024;
    public const string Filename = "fixture-api.yaml";
    public const string Markdown = "Popis API\n\nFixture API · v1.0 · Autor: Jana Nováková";
    private const string Uploader = "Jana Nováková";
    private const string SourceHeader = "openapi: 3.1.0\ninfo:\n  title: \"Položky \\\"API\\\" C:\\\\data\"\n  version: '1.0'\npaths: {}\n#";

    /// <summary>Stores quotes and non-ASCII text unescaped, as the browser's JSON.stringify does.</summary>
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A YAML source of exactly <see cref="MaxSourceBytes"/> UTF-8 bytes with quotes, backslashes and non-ASCII text.</summary>
    public static string LargestSource { get; } =
        SourceHeader + new string('x', MaxSourceBytes - Encoding.UTF8.GetByteCount(SourceHeader) - 1) + "\n";

    public static string Rich(string? source = null, string? mode = "interactive", string? uploadedBy = Uploader, string text = "Popis API")
    {
        var data = new JsonObject { ["version"] = 1, ["filename"] = Filename, ["source"] = source ?? LargestSource };
        if (uploadedBy is not null) data["uploadedBy"] = uploadedBy;
        if (mode is not null) data["mode"] = mode;
        return new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["document"] = new JsonObject
            {
                ["type"] = "doc",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) },
                    new JsonObject { ["type"] = "openapi", ["attrs"] = new JsonObject { ["data"] = data } })
            },
            ["plugins"] = new JsonArray(new JsonObject { ["id"] = "openapi", ["version"] = 1 })
        }.ToJsonString(Unescaped);
    }

    /// <summary>An OpenAPI page whose content JSON has exactly <paramref name="length"/> UTF-16 characters (the unit Rules counts): the
    /// largest source the editor accepts and a paragraph padded to the length.</summary>
    public static string RichOfLength(int length)
    {
        var overhead = Rich(text: "").Length;
        return Rich(text: new string('a', length - overhead));
    }

    /// <summary>The editor projection the collaboration relay flushes: the envelope's <c>document</c>.</summary>
    public static JsonElement Projection(string rich)
    {
        using var json = JsonDocument.Parse(rich);
        return json.RootElement.GetProperty("document").Clone();
    }

    public static JsonObject Block(string rich)
    {
        return JsonNode.Parse(rich)!["document"]!["content"]!.AsArray()
            .Single(node => node!["type"]!.GetValue<string>() == "openapi")!.AsObject();
    }
}
