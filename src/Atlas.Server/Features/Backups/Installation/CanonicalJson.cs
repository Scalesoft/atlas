using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>The one way a backup file is serialized: keys in ordinal order, one member per line, "\n" on every platform and
/// numbers in the text PostgreSQL gave them. The same row therefore gives the same bytes on every host, and a changed column
/// changes one line.</summary>
internal static class CanonicalJson
{
    internal const int MaxDepth = 256;
    private static readonly JsonNodeOptions NodeOptions = new();
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth };

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        NewLine = "\n",
        MaxDepth = MaxDepth,
        // Files are data in a repository, never HTML: Czech text stays readable in diffs.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonObject Parse(ReadOnlySpan<byte> json)
        => JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonObject ?? throw new InvalidDataException("Soubor zálohy není objekt JSON.");

    public static JsonObject Parse(string json)
        => JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonObject ?? throw new InvalidDataException("Soubor zálohy není objekt JSON.");

    public static byte[] Write(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            Write(writer, node);
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var (name, member) in value.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    Write(writer, member);
                }
                writer.WriteEndObject();
                break;
            case JsonArray items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
