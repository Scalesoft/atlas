using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The opaque paging cursor of the read tools: base64url of a small JSON object naming the tool kind (a cursor of one tool is invalid
/// for another) and its position. Nothing in it is a secret or a decision: every call decides access again, and a stale or forged
/// cursor only moves the window or answers 400 invalid_argument.
/// </summary>
public static class McpCursor
{
    public const string Invalid = "Neplatný cursor. Použijte hodnotu nextCursor z předchozího výsledku beze změny.";

    public static string Create(string kind, JsonObject position)
    {
        position["k"] = kind;
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(position.ToJsonString()));
    }

    /// <summary>The position of a cursor of this kind; null for no cursor. 400 invalid_argument for anything else.</summary>
    public static JsonObject? Read(string? cursor, string kind)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }
        try
        {
            if (cursor.Length > 512 || JsonNode.Parse(Base64Url.DecodeFromChars(cursor)) is not JsonObject position
                || position["k"]?.GetValue<string>() != kind)
            {
                throw new ApiException(400, Invalid, "invalid_argument");
            }
            return position;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new ApiException(400, Invalid, "invalid_argument");
        }
    }

    public static int Offset(string? cursor, string kind, int max = 100_000)
    {
        var position = Read(cursor, kind);
        if (position is null)
        {
            return 0;
        }
        return Int(position, "o", 0, max);
    }

    public static int Int(JsonObject position, string name, int min, int max)
    {
        try
        {
            if (position[name] is JsonValue value && value.TryGetValue<int>(out var number) && number >= min && number <= max)
            {
                return number;
            }
        }
        catch (InvalidOperationException)
        {
        }
        throw new ApiException(400, Invalid, "invalid_argument");
    }
}
