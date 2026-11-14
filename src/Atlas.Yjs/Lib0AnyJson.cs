using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Atlas.Yjs;

/// <summary>Converts one lib0 Any value (already validated by <see cref="Lib0Reader.SkipAny"/>) to JSON.</summary>
internal static class Lib0AnyJson
{
    /// <summary>Host hooks: undefined and non-finite numbers become null, binaries base64 and bigints numbers.</summary>
    public static JsonElement ToJson(ReadOnlySpan<byte> value) => ToJson(value, strict: false);

    /// <summary>
    /// <paramref name="strict"/>: the value as the editor's document data must be (plain JSON: finite numbers, no undefined,
    /// binary or bigint anywhere), else <see cref="ContentRejectionReason.InvalidAttributeValue"/>.
    /// </summary>
    public static JsonElement ToJson(ReadOnlySpan<byte> value, bool strict)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(value, writer, strict);
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>A top-level undefined (what Yjs stores for an attribute set to undefined), which the editor reads as absent.</summary>
    public static bool IsUndefined(ReadOnlySpan<byte> value) => value.Length == 1 && value[0] == 127;

    public static void Write(ReadOnlySpan<byte> value, Utf8JsonWriter writer, bool strict)
    {
        var reader = new Lib0Reader(value);
        Write(ref reader, writer, 1, strict);
    }

    private static void Write(ref Lib0Reader reader, Utf8JsonWriter writer, int depth, bool strict)
    {
        if (depth > YjsLimits.MaxValueDepth) throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "value nesting is too deep");
        var tag = reader.ReadByte();
        if (strict && tag is 127 or 122 or 116)
            throw new ContentRejectedException(ContentRejectionReason.InvalidAttributeValue, "attribute values must be plain JSON data");
        switch (tag)
        {
            case 127: // undefined
            case 126:
                writer.WriteNullValue();
                return;
            case 125:
                writer.WriteNumberValue(reader.ReadVarInt());
                return;
            case 124:
                WriteDouble(writer, BinaryPrimitives.ReadSingleBigEndian(reader.ReadBytes(4)), strict);
                return;
            case 123:
                WriteDouble(writer, BinaryPrimitives.ReadDoubleBigEndian(reader.ReadBytes(8)), strict);
                return;
            case 122:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8)));
                return;
            case 121:
                writer.WriteBooleanValue(false);
                return;
            case 120:
                writer.WriteBooleanValue(true);
                return;
            case 119:
                writer.WriteStringValue(reader.ReadUtf8());
                return;
            case 118:
            {
                var count = reader.ReadCount();
                writer.WriteStartObject();
                for (var index = 0; index < count; index++)
                {
                    writer.WritePropertyName(reader.ReadUtf8());
                    Write(ref reader, writer, depth + 1, strict);
                }
                writer.WriteEndObject();
                return;
            }
            case 117:
            {
                var count = reader.ReadCount();
                writer.WriteStartArray();
                for (var index = 0; index < count; index++) Write(ref reader, writer, depth + 1, strict);
                writer.WriteEndArray();
                return;
            }
            case 116:
                writer.WriteBase64StringValue(reader.ReadVarBytes());
                return;
            default:
                throw new ContentRejectedException(ContentRejectionReason.Malformed, "unknown Any value tag");
        }
    }

    // JSON.stringify semantics for non-finite numbers.
    private static void WriteDouble(Utf8JsonWriter writer, double value, bool strict)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else if (strict) throw new ContentRejectedException(ContentRejectionReason.InvalidAttributeValue, "attribute values must hold finite numbers");
        else writer.WriteNullValue();
    }
}
