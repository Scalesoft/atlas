using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Atlas.Yjs;

/// <summary>
/// JavaScript values as yjs and ProseMirror hold them, so the port can keep their semantics: <c>null</c>, <see cref="JsUndefined"/>,
/// <see cref="bool"/>, <see cref="double"/>, <see cref="string"/>, <see cref="JsBigInt"/>, <see cref="byte"/>[] (Uint8Array),
/// <see cref="JsArray"/> and <see cref="JsObject"/>. Objects and arrays compare by reference, as <c>===</c> does.
/// </summary>
internal static class Js
{
    /// <summary>JavaScript's <c>===</c>.</summary>
    public static bool StrictEquals(object? a, object? b) => a switch
    {
        null => b is null,
        double x => b is double y && x == y,
        string x => b is string y && string.Equals(x, y, StringComparison.Ordinal),
        bool x => b is bool y && x == y,
        JsBigInt x => b is JsBigInt y && x.Value == y.Value,
        _ => ReferenceEquals(a, b),
    };

    /// <summary><c>typeof value === 'object' &amp;&amp; value !== null</c>.</summary>
    public static bool IsObject(object? value) => value is JsObject or JsArray or byte[];

    /// <summary><c>Object.keys(value)</c> (a primitive has none but a string, whose keys are its indexes).</summary>
    public static IEnumerable<string> Keys(object? value) => value switch
    {
        JsObject obj => obj.Keys,
        JsArray array => Enumerable.Range(0, array.Count).Select(index => index.ToString(CultureInfo.InvariantCulture)),
        byte[] bytes => Enumerable.Range(0, bytes.Length).Select(index => index.ToString(CultureInfo.InvariantCulture)),
        string text => Enumerable.Range(0, text.Length).Select(index => index.ToString(CultureInfo.InvariantCulture)),
        _ => [],
    };

    /// <summary><c>value[key]</c> of a value <see cref="Keys"/> enumerates; <see cref="JsUndefined"/> when absent.</summary>
    public static object? Get(object? value, string key)
    {
        if (value is JsObject obj) return obj.TryGetValue(key, out var found) ? found : JsUndefined.Value;
        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index.ToString(CultureInfo.InvariantCulture) != key)
            return JsUndefined.Value;
        return value switch
        {
            JsArray array when index < array.Count => array[index],
            byte[] bytes when index < bytes.Length => (double)bytes[index],
            string text when index < text.Length => text[index].ToString(),
            _ => JsUndefined.Value,
        };
    }

    /// <summary>JSON.parse of a JSON value.</summary>
    public static object? FromJson(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => ObjectFromJson(json),
        JsonValueKind.Array => ArrayFromJson(json),
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static JsObject ObjectFromJson(JsonElement json)
    {
        var obj = new JsObject();
        foreach (var property in json.EnumerateObject()) obj[property.Name] = FromJson(property.Value);
        return obj;
    }

    private static JsArray ArrayFromJson(JsonElement json)
    {
        var array = new JsArray();
        foreach (var item in json.EnumerateArray()) array.Add(FromJson(item));
        return array;
    }

    /// <summary>JSON.parse of UTF-8 JSON text (a format's value).</summary>
    public static object? ParseJson(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = YjsLimits.MaxValueDepth + 1 });
        using var document = JsonDocument.ParseValue(ref reader);
        return FromJson(document.RootElement);
    }

    /// <summary>lib0 decoding.readAny.</summary>
    public static object? ReadAny(ReadOnlySpan<byte> value)
    {
        var reader = new Lib0Reader(value);
        return ReadAny(ref reader, 1);
    }

    private static object? ReadAny(ref Lib0Reader reader, int depth)
    {
        if (depth > YjsLimits.MaxValueDepth) throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "value nesting is too deep");
        switch (reader.ReadByte())
        {
            case 127: return JsUndefined.Value;
            case 126: return null;
            case 125: return (double)reader.ReadVarInt();
            case 124: return (double)BinaryPrimitives.ReadSingleBigEndian(reader.ReadBytes(4));
            case 123: return BinaryPrimitives.ReadDoubleBigEndian(reader.ReadBytes(8));
            case 122: return new JsBigInt(BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8)));
            case 121: return false;
            case 120: return true;
            case 119: return Encoding.UTF8.GetString(reader.ReadUtf8());
            case 118:
            {
                var count = reader.ReadCount();
                var obj = new JsObject();
                for (var index = 0; index < count; index++)
                {
                    var key = Encoding.UTF8.GetString(reader.ReadUtf8());
                    obj[key] = ReadAny(ref reader, depth + 1);
                }
                return obj;
            }
            case 117:
            {
                var count = reader.ReadCount();
                var array = new JsArray();
                for (var index = 0; index < count; index++) array.Add(ReadAny(ref reader, depth + 1));
                return array;
            }
            case 116: return reader.ReadVarBytes().ToArray();
            default: throw new ContentRejectedException(ContentRejectionReason.Malformed, "unknown Any value tag");
        }
    }

    /// <summary>JSON.stringify of a value it can write (what ContentFormat.write stores).</summary>
    public static string Stringify(object? value)
    {
        var output = new StringBuilder();
        Stringify(output, value);
        return output.ToString();
    }

    private static void Stringify(StringBuilder output, object? value)
    {
        switch (value)
        {
            case null or JsUndefined:
                output.Append("null");
                return;
            case bool flag:
                output.Append(flag ? "true" : "false");
                return;
            case double number:
                output.Append(double.IsFinite(number) ? JsJson.Number(number) : "null");
                return;
            case string text:
                JsJson.String(output, text);
                return;
            case JsArray array:
                output.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) output.Append(',');
                    Stringify(output, array[index]);
                }
                output.Append(']');
                return;
            case JsObject obj:
                output.Append('{');
                var first = true;
                foreach (var key in obj.Keys)
                {
                    var item = obj[key];
                    if (item is JsUndefined) continue;
                    if (!first) output.Append(',');
                    first = false;
                    JsJson.String(output, key);
                    output.Append(':');
                    Stringify(output, item);
                }
                output.Append('}');
                return;
            default:
                throw new ContentRejectedException(ContentRejectionReason.InvalidAttributeValue, "a value JSON.stringify cannot write");
        }
    }
}

/// <summary>JavaScript's <c>undefined</c>.</summary>
internal sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();

    private JsUndefined()
    {
    }
}

/// <summary>A BigInt (lib0 Any 122).</summary>
internal sealed record JsBigInt(long Value);

/// <summary>A JavaScript array.</summary>
internal sealed class JsArray : List<object?>;

/// <summary>
/// A plain JavaScript object: an assigned key keeps its place, and keys enumerate as OrdinaryOwnPropertyKeys orders them (array
/// index keys ascending first, then the others in insertion order).
/// </summary>
internal sealed class JsObject
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private string[]? _keys;

    public int Count => _values.Count;

    public IReadOnlyList<string> Keys => _keys ??= [.. _order.Where(IsArrayIndex).OrderBy(key => uint.Parse(key, CultureInfo.InvariantCulture)),
        .. _order.Where(key => !IsArrayIndex(key))];

    public object? this[string key]
    {
        get => _values[key];
        set
        {
            if (_values.TryAdd(key, value))
            {
                _order.Add(key);
                _keys = null;
            }
            else _values[key] = value;
        }
    }

    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <summary>Object.assign({}, this).</summary>
    public JsObject Copy()
    {
        var copy = new JsObject();
        foreach (var key in Keys) copy[key] = _values[key];
        return copy;
    }

    /// <summary>A canonical numeric string below 2^32 - 1.</summary>
    private static bool IsArrayIndex(string key)
        => key.Length is > 0 and <= 10 && (key.Length == 1 || key[0] != '0') && key.All(char.IsAsciiDigit)
            && ulong.Parse(key, CultureInfo.InvariantCulture) < uint.MaxValue;
}

/// <summary>lib0 encoding (v1): the primitives the Yjs update encoder and lib0's writeAny write.</summary>
internal sealed class Lib0Writer
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    /// <summary>lib0 encoding.write.</summary>
    public void Write(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
    }

    public void Write(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

    /// <summary>lib0 encoding.writeVarUint.</summary>
    public void WriteVarUint(ulong value)
    {
        while (value > 0x7F)
        {
            Write((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }
        Write((byte)value);
    }

    /// <summary>lib0 encoding.writeVarInt (lib0 0.2.117): sign bit in the first byte, which holds six bits of the value.</summary>
    public void WriteVarInt(double number)
    {
        var negative = number < 0 || (number == 0 && double.IsNegative(number));
        var value = (ulong)Math.Abs(number);
        Write((byte)((value > 0x3F ? 0x80 : 0) | (negative ? 0x40 : 0) | (byte)(value & 0x3F)));
        value >>= 6;
        while (value > 0)
        {
            Write((byte)((value > 0x7F ? 0x80 : 0) | (byte)(value & 0x7F)));
            value >>= 7;
        }
    }

    /// <summary>lib0 encoding.writeVarString: UTF-8 as TextEncoder writes it (a lone surrogate becomes U+FFFD).</summary>
    public void WriteVarString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarUint((ulong)bytes.Length);
        Write(bytes);
    }

    /// <summary>lib0 encoding.writeAny (lib0 0.2.117).</summary>
    public void WriteAny(object? data)
    {
        switch (data)
        {
            case string text:
                Write(119);
                WriteVarString(text);
                return;
            case double number:
                if (Math.Floor(number) == number && Math.Abs(number) <= 0x7FFFFFFF)
                {
                    Write(125);
                    WriteVarInt(number);
                }
                else if ((double)(float)number == number)
                {
                    Write(124);
                    Span<byte> single = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(single, (float)number);
                    Write(single);
                }
                else
                {
                    Write(123);
                    Span<byte> bytes = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(bytes, number);
                    Write(bytes);
                }
                return;
            case JsBigInt bigint:
            {
                Write(122);
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, bigint.Value);
                Write(bytes);
                return;
            }
            case null:
                Write(126);
                return;
            case JsArray array:
                Write(117);
                WriteVarUint((ulong)array.Count);
                foreach (var item in array) WriteAny(item);
                return;
            case byte[] binary:
                Write(116);
                WriteVarUint((ulong)binary.Length);
                Write(binary);
                return;
            case JsObject obj:
                Write(118);
                WriteVarUint((ulong)obj.Count);
                foreach (var key in obj.Keys)
                {
                    WriteVarString(key);
                    WriteAny(obj[key]);
                }
                return;
            case bool flag:
                Write(flag ? (byte)120 : (byte)121);
                return;
            default:
                Write(127);
                return;
        }
    }
}
