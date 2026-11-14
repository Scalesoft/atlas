using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Atlas.Yjs;

/// <summary>
/// Writes JSON as JavaScript's JSON.stringify writes it: strings escaped only where it must (quotes, backslashes, control
/// characters, lone surrogates), numbers as Number.prototype.toString writes them, object keys in the order a JavaScript object
/// enumerates them. <c>YjsDocumentMaterializer</c> (Atlas.Markdown.Sync)'s output so equals the editor's byte for byte.
/// </summary>
internal static class JsJson
{
    public static void String(StringBuilder output, string value)
    {
        output.Append('"');
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            string escape;
            if (c == '"') escape = "\\\"";
            else if (c == '\\') escape = "\\\\";
            else if (c < 0x20) escape = c switch { '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => $"\\u{(int)c:x4}" };
            else if (!char.IsSurrogate(c)) continue;
            else if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }
            else escape = $"\\u{(int)c:x4}";
            output.Append(value, start, index - start).Append(escape);
            start = index + 1;
        }
        output.Append(value, start, value.Length - start).Append('"');
    }

    /// <summary>Number::toString of a finite number: the shortest digits that round-trip, laid out as JavaScript lays them out.</summary>
    public static string Number(double value)
    {
        if (value == 0) return "0"; // -0 as well
        if (!double.IsFinite(value)) throw Invalid("numbers must be finite");
        // .NET's "R" has the same shortest round-trip digits; only their layout differs.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var sign = text[0] == '-' ? 1 : 0;
        var exponentAt = text.IndexOf('E', StringComparison.Ordinal);
        var mantissa = text.AsSpan(sign, (exponentAt < 0 ? text.Length : exponentAt) - sign);
        var point = mantissa.IndexOf('.');
        var all = point < 0 ? mantissa.ToString() : string.Concat(mantissa[..point], mantissa[(point + 1)..]);
        var digits = all.Trim('0');
        // value = 0.digits × 10^n
        var n = (point < 0 ? mantissa.Length : point) - (all.Length - all.TrimStart('0').Length)
            + (exponentAt < 0 ? 0 : int.Parse(text.AsSpan(exponentAt + 1), CultureInfo.InvariantCulture));
        var k = digits.Length;
        var output = new StringBuilder(sign == 1 ? "-" : "");
        if (k <= n && n <= 21) output.Append(digits).Append('0', n - k);
        else if (0 < n && n <= 21) output.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        else if (-6 < n && n <= 0) output.Append("0.").Append('0', -n).Append(digits);
        else
        {
            output.Append(digits[0]);
            if (k > 1) output.Append('.').Append(digits, 1, k - 1);
            output.Append('e').Append(n - 1 > 0 ? '+' : '-').Append(Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    /// <summary>A JSON value: a format's JSON, a schema default, a lib0 value read by <see cref="Lib0AnyJson"/>.</summary>
    /// <param name="sorted">Object keys sorted, a canonical form: equal for the values ProseMirror's compareDeep finds equal.</param>
    public static void Value(StringBuilder output, JsonElement value, bool sorted = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Object(output, value, sorted);
                return;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) output.Append(',');
                    first = false;
                    Value(output, item, sorted);
                }
                output.Append(']');
                return;
            case JsonValueKind.String:
                String(output, Text(value));
                return;
            case JsonValueKind.Number:
                output.Append(Number(value.TryGetDouble(out var number) ? number : double.NaN));
                return;
            case JsonValueKind.True:
                output.Append("true");
                return;
            case JsonValueKind.False:
                output.Append("false");
                return;
            default:
                output.Append("null");
                return;
        }
    }

    /// <summary>A lib0 Any value as plain JSON data; scalars directly, anything else through <see cref="Lib0AnyJson"/> (strict).</summary>
    public static void Any(StringBuilder output, byte[] value)
    {
        var reader = new Lib0Reader(value);
        switch (reader.ReadByte())
        {
            case 126:
                output.Append("null");
                return;
            case 120:
                output.Append("true");
                return;
            case 121:
                output.Append("false");
                return;
            case 125:
                output.Append(reader.ReadVarInt().ToString(CultureInfo.InvariantCulture));
                return;
            case 119:
                String(output, Encoding.UTF8.GetString(reader.ReadUtf8()));
                return;
            default:
                Value(output, Lib0AnyJson.ToJson(value, strict: true));
                return;
        }
    }

    /// <summary>The value of a key as a JavaScript property read gives it: the last of repeated keys.</summary>
    public static bool TryGetLast(JsonElement value, string key, out JsonElement found)
    {
        found = default;
        var any = false;
        foreach (var property in value.EnumerateObject())
            if (property.NameEquals(key))
            {
                found = property.Value;
                any = true;
            }
        return any;
    }

    private static void Object(StringBuilder output, JsonElement value, bool sorted)
    {
        // JSON.parse and lib0 assign keys in order, so a repeated key keeps its first place and takes its last value; an object
        // enumerates its array index keys first, ascending (OrdinaryOwnPropertyKeys).
        var keys = new List<string>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            // lib0 sets the prototype for it and JSON.stringify drops it, JSON.parse keeps it as a key.
            if (property.NameEquals("__proto__")) throw Invalid("an object key __proto__");
            if (values.TryAdd(property.Name, property.Value)) keys.Add(property.Name);
            else values[property.Name] = property.Value;
        }
        var order = sorted ? keys.Order(StringComparer.Ordinal)
            : keys.Where(IsArrayIndex).OrderBy(key => uint.Parse(key, CultureInfo.InvariantCulture)).Concat(keys.Where(key => !IsArrayIndex(key)));
        output.Append('{');
        var first = true;
        foreach (var key in order)
        {
            if (!first) output.Append(',');
            first = false;
            String(output, key);
            output.Append(':');
            Value(output, values[key], sorted);
        }
        output.Append('}');
    }

    /// <summary>A canonical numeric string below 2^32 - 1.</summary>
    private static bool IsArrayIndex(string key)
        => key.Length is > 0 and <= 10 && (key.Length == 1 || key[0] != '0') && key.All(char.IsAsciiDigit)
            && ulong.Parse(key, CultureInfo.InvariantCulture) < uint.MaxValue;

    private static string Text(JsonElement value)
    {
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw Invalid("a string with a lone surrogate");
        }
    }

    private static ContentRejectedException Invalid(string detail) => new(ContentRejectionReason.InvalidAttributeValue, detail);
}
