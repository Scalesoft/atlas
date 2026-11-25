using System.Text;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

internal static class AuthorityAwareness
{
    internal sealed record Validated(long ClientId, long Clock, byte[] Payload);
    /// <param name="color">The colour the participant holds (<see cref="CollaboratorColors"/>); never the client's own claim.</param>
    /// <param name="presenceOnly">A read-only participant (a reader of a live page): only its identity goes out, no cursor or selection.</param>
    internal static Validated Validate(byte[] payload, CollabUser user, int maxBytes, bool presenceOnly = false, string? color = null)
    {
        if (payload is null || payload.Length > maxBytes) throw new InvalidDataException("Awareness exceeds its byte budget.");
        var offset = 0;
        if (Read(payload, ref offset) != 1) throw new InvalidDataException("Awareness must contain exactly one owned state.");
        var client = Read(payload, ref offset);
        var clock = Read(payload, ref offset);
        var length = Read(payload, ref offset);
        if (length > int.MaxValue || length != payload.Length - offset) throw new InvalidDataException("Invalid awareness string framing.");
        JsonDocument document;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload, offset, (int)length);
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("Invalid awareness JSON.", error); }
        using (document)
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Null) writer.WriteNullValue();
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate awareness state field.");
                        if (property.Name == "user" || presenceOnly) continue;
                        property.WriteTo(writer);
                    }
                    writer.WritePropertyName("user"); writer.WriteStartObject();
                    writer.WriteString("id", user.Id); writer.WriteString("name", user.Name);
                    writer.WriteString("color", color ?? user.Color ?? "#888888"); writer.WriteEndObject(); writer.WriteEndObject();
                }
                else throw new InvalidDataException("Awareness state must be an object or null.");
            }
            var json = buffer.ToArray();
            using var framed = new MemoryStream();
            Write(framed, 1); Write(framed, client); Write(framed, clock); Write(framed, json.Length); framed.Write(json);
            if (framed.Length > maxBytes) throw new InvalidDataException("Verified awareness exceeds its byte budget.");
            return new Validated(client, clock, framed.ToArray());
        }
    }
    /// <summary>The removal of a client's state, one clock after the last one relayed, as y-protocols' own leave encodes it.</summary>
    internal static byte[] Removal(long clientId, long clock)
    {
        using var framed = new MemoryStream();
        Write(framed, 1); Write(framed, clientId); Write(framed, clock); Write(framed, 4); framed.Write("null"u8);
        return framed.ToArray();
    }
    private static long Read(byte[] bytes, ref int offset)
    {
        long result = 0, multiplier = 1;
        for (var count = 0; count < 8; count++)
        {
            if (offset >= bytes.Length) break;
            var value = bytes[offset++];
            result = checked(result + (value & 127) * multiplier);
            if (result > 9_007_199_254_740_991) throw new InvalidDataException("Unsafe awareness integer.");
            if ((value & 128) == 0) return result;
            multiplier = checked(multiplier * 128);
        }
        throw new InvalidDataException("Invalid awareness integer framing.");
    }
    private static void Write(Stream stream, long value)
    {
        while (value > 127) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }
}
