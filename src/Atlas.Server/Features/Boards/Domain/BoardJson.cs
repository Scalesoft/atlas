using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>The changes of a batch as the operation log keeps them: the typed records in the API's JSON, so a retry with the same
/// changes (in any member order or spacing, props canonical: BoardEditing.Normalize) serializes to the same text and a different
/// one does not.</summary>
public static class BoardJson
{
    // Text as raw UTF-8 like stored rich JSON (RichDocument.JsonWrite): a Czech note is not inflated by \u escapes, and the
    // catch-up's byte budget counts what is really stored. Never written into HTML; the API re-serializes what it reads.
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(IReadOnlyList<BoardChange> changes)
    {
        return JsonSerializer.Serialize(changes, s_options);
    }

    public static IReadOnlyList<BoardChange> Deserialize(string json)
    {
        return JsonSerializer.Deserialize<BoardChange[]>(json, s_options) ?? [];
    }

    /// <summary>Stored JSON text as an element of an answer.</summary>
    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A props object (or any node) in the same text form as the log.</summary>
    public static string Write(JsonNode node)
    {
        return node.ToJsonString(s_options);
    }
}
