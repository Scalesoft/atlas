using System.Buffers.Binary;
using System.Text;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// The KDBX 4 typed key/value encoding used for KDF parameters. Parsing is strict: every length is checked
/// against the remaining input, fixed-size values must have their exact size and keys must be unique.
/// </summary>
internal sealed class KdbxVariantDictionary
{
    private const byte TypeEnd = 0x00;
    private const byte TypeUInt32 = 0x04;
    private const byte TypeUInt64 = 0x05;
    private const byte TypeBool = 0x08;
    private const byte TypeInt32 = 0x0C;
    private const byte TypeInt64 = 0x0D;
    private const byte TypeString = 0x18;
    private const byte TypeBytes = 0x42;

    private readonly Dictionary<string, object> m_values = new(StringComparer.Ordinal);

    public static KdbxVariantDictionary Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw KdbxException.Corrupt();
        }
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if ((version & 0xFF00) != (KdbxFormat.VariantDictionaryVersion & 0xFF00))
        {
            throw KdbxException.Unsupported("The key derivation parameters use an unknown encoding. Save the database with a current KeePass version.");
        }
        var result = new KdbxVariantDictionary();
        var position = 2;
        while (true)
        {
            if (position >= data.Length)
            {
                throw KdbxException.Corrupt();
            }
            var type = data[position++];
            if (type == TypeEnd)
            {
                if (position != data.Length)
                {
                    throw KdbxException.Corrupt();
                }
                return result;
            }
            var key = Encoding.UTF8.GetString(ReadSized(data, ref position));
            var value = ReadSized(data, ref position);
            object parsed = type switch
            {
                TypeUInt32 when value.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
                TypeUInt64 when value.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(value),
                TypeBool when value.Length == 1 => value[0] != 0,
                TypeInt32 when value.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(value),
                TypeInt64 when value.Length == 8 => BinaryPrimitives.ReadInt64LittleEndian(value),
                TypeString => Encoding.UTF8.GetString(value),
                TypeBytes => value.ToArray(),
                _ => throw KdbxException.Corrupt(),
            };
            if (!result.m_values.TryAdd(key, parsed))
            {
                throw KdbxException.Corrupt();
            }
        }
    }

    public uint? UInt32(string key) => Get<uint>(key);

    public ulong? UInt64(string key) => Get<ulong>(key);

    public byte[]? Bytes(string key) => m_values.TryGetValue(key, out var value) ? value as byte[] ?? throw KdbxException.Corrupt() : null;

    public void Set(string key, uint value) => m_values[key] = value;

    public void Set(string key, ulong value) => m_values[key] = value;

    public void Set(string key, byte[] value) => m_values[key] = value;

    public byte[] ToArray()
    {
        using var output = new MemoryStream();
        Span<byte> version = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(version, KdbxFormat.VariantDictionaryVersion);
        output.Write(version);
        foreach (var (key, value) in m_values)
        {
            byte type;
            byte[] bytes;
            switch (value)
            {
                case uint u:
                    type = TypeUInt32;
                    bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes, u);
                    break;
                case ulong u:
                    type = TypeUInt64;
                    bytes = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes, u);
                    break;
                case byte[] b:
                    type = TypeBytes;
                    bytes = b;
                    break;
                default:
                    throw new InvalidOperationException("Unsupported variant dictionary value.");
            }
            output.WriteByte(type);
            WriteSized(output, Encoding.UTF8.GetBytes(key));
            WriteSized(output, bytes);
        }
        output.WriteByte(TypeEnd);
        return output.ToArray();
    }

    private T? Get<T>(string key)
        where T : struct
    {
        if (!m_values.TryGetValue(key, out var value))
        {
            return null;
        }
        return value is T typed ? typed : throw KdbxException.Corrupt();
    }

    private static ReadOnlySpan<byte> ReadSized(ReadOnlySpan<byte> data, ref int position)
    {
        if (data.Length - position < 4)
        {
            throw KdbxException.Corrupt();
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
        position += 4;
        if (length < 0 || length > data.Length - position)
        {
            throw KdbxException.Corrupt();
        }
        var slice = data.Slice(position, length);
        position += length;
        return slice;
    }

    private static void WriteSized(Stream output, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        output.Write(length);
        output.Write(value);
    }
}
