using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Atlas.Yjs;

/// <summary>
/// Bounds-checked lib0 decoding primitives, as used by the Yjs v1 update decoder. Every failure is a
/// <see cref="ContentRejectedException"/>. The reader never accepts input that lib0 in the peers would decode
/// differently or fail on: integers beyond <c>Number.MAX_SAFE_INTEGER</c>, invalid UTF-8 (lib0 uses a fatal
/// <c>TextDecoder</c>), invalid JSON (<c>JSON.parse</c>) and unknown Any tags are all rejected.
/// </summary>
internal ref struct Lib0Reader(ReadOnlySpan<byte> data)
{
    private const ulong MaxSafeInteger = (1UL << 53) - 1;
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Position => _position;
    public readonly bool AtEnd => _position == _data.Length;
    private readonly int Remaining => _data.Length - _position;

    public byte ReadByte()
    {
        if (_position >= _data.Length) throw Malformed("unexpected end of payload");
        return _data[_position++];
    }

    /// <summary>lib0 <c>readVarUint</c>, at most eight bytes and at most 2^53 - 1.</summary>
    public ulong ReadVarUint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 56; shift += 7)
        {
            var next = ReadByte();
            value |= (ulong)(next & 0x7F) << shift;
            if (value > MaxSafeInteger) throw Malformed("integer exceeds the JavaScript safe range");
            if (next < 0x80) return value;
        }
        throw Malformed("variable-length integer is too long");
    }

    /// <summary>A clock or struct length; Yjs clocks never reach 2^32.</summary>
    public uint ReadClock()
    {
        var value = ReadVarUint();
        if (value > uint.MaxValue) throw Malformed("clock out of range");
        return (uint)value;
    }

    /// <summary>A count of elements that each take at least one byte, so it never exceeds the rest of the payload.</summary>
    public int ReadCount()
    {
        var value = ReadVarUint();
        if (value > (ulong)Remaining) throw Malformed("count exceeds the payload");
        return (int)value;
    }

    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        if (length < 0 || length > Remaining) throw Malformed("length exceeds the payload");
        var bytes = _data.Slice(_position, length);
        _position += length;
        return bytes;
    }

    public ReadOnlySpan<byte> ReadVarBytes() => ReadBytes(ReadCount());

    /// <summary>lib0 <c>readVarString</c>: length-prefixed, strictly valid UTF-8.</summary>
    public ReadOnlySpan<byte> ReadUtf8()
    {
        var bytes = ReadVarBytes();
        if (!Utf8.IsValid(bytes)) throw Malformed("invalid UTF-8");
        return bytes;
    }

    /// <summary>A string content; returns its length in UTF-16 code units, which is its Yjs clock length.</summary>
    public int ReadStringLength() => Encoding.UTF8.GetCharCount(ReadUtf8());

    /// <summary>An element name, format key, attribute key or root name.</summary>
    public string ReadName()
    {
        var bytes = ReadUtf8();
        if (bytes.Length > YjsLimits.MaxNameBytes)
            throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "name is too long");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>A string the peers pass to <c>JSON.parse</c>.</summary>
    public void ReadJson()
    {
        var reader = new Utf8JsonReader(ReadUtf8(), new JsonReaderOptions { MaxDepth = YjsLimits.MaxValueDepth });
        try
        {
            while (reader.Read()) { }
        }
        catch (JsonException)
        {
            throw Malformed("invalid JSON value");
        }
    }

    /// <summary>lib0 <c>readAny</c> without materializing the value.</summary>
    public void SkipAny(int depth)
    {
        if (depth > YjsLimits.MaxValueDepth)
            throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "value nesting is too deep");
        switch (ReadByte())
        {
            case 127: // undefined
            case 126: // null
            case 121: // false
            case 120: // true
                return;
            case 125:
                ReadVarInt();
                return;
            case 124: // float32
                ReadBytes(4);
                return;
            case 123: // float64
            case 122: // bigint64
                ReadBytes(8);
                return;
            case 119:
                ReadUtf8();
                return;
            case 118:
            {
                var count = ReadCount();
                for (var index = 0; index < count; index++)
                {
                    ReadUtf8();
                    SkipAny(depth + 1);
                }
                return;
            }
            case 117:
            {
                var count = ReadCount();
                for (var index = 0; index < count; index++) SkipAny(depth + 1);
                return;
            }
            case 116:
                ReadVarBytes();
                return;
            default:
                throw Malformed("unknown Any value tag");
        }
    }

    /// <summary>lib0 <c>readVarInt</c>: six value bits and a sign in the first byte, then seven bits per byte.</summary>
    public long ReadVarInt()
    {
        var next = ReadByte();
        var negative = (next & 0x40) != 0;
        var value = (ulong)(next & 0x3F);
        var shift = 6;
        while (next >= 0x80)
        {
            next = ReadByte();
            var bits = (ulong)(next & 0x7F);
            if (bits != 0 && shift >= 53) throw Malformed("integer exceeds the JavaScript safe range");
            value |= bits << shift;
            if (value > MaxSafeInteger) throw Malformed("integer exceeds the JavaScript safe range");
            shift += 7;
            if (shift > 62) throw Malformed("variable-length integer is too long");
        }
        return negative ? -(long)value : (long)value;
    }

    private static ContentRejectedException Malformed(string detail) => new(ContentRejectionReason.Malformed, detail);
}
