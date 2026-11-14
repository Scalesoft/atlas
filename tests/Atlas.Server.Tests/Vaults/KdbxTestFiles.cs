using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Kdbx;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// KeePass-generated fixtures (tests/fixtures/kdbx, see generate-fixtures.ps1) and byte-level editing of the
/// unencrypted outer header, for tests that must reach a specific check with an otherwise valid file.
/// </summary>
internal static class KdbxTestFiles
{
    private static readonly Lazy<JsonElement> ManifestDocument = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json"))).RootElement.Clone());

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "fixtures", "kdbx");

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Folder, name));

    public static JsonElement Fixture(string name) =>
        ManifestDocument.Value.GetProperty("fixtures").EnumerateArray().Single(f => f.GetProperty("file").GetString() == name);

    public static IEnumerable<JsonElement> Fixtures => ManifestDocument.Value.GetProperty("fixtures").EnumerateArray();

    public static string Generator => ManifestDocument.Value.GetProperty("generator").GetString()!;

    public static string Password(string name) => Fixture(name).GetProperty("password").GetString()!;

    public static Guid Uuid(string hex) => new(Convert.FromHexString(hex));

    public static string Hex(Guid uuid) => Convert.ToHexString(uuid.ToByteArray());

    public static KdbxReader Reader(VaultOptions? options = null, KdbxKeyDerivation? keyDerivation = null) =>
        new(options ?? new VaultOptions(), keyDerivation ?? new KdbxKeyDerivation());

    public static KdbxException ReadFails(byte[] file, string password, VaultOptions? options = null, bool storedKey = false)
    {
        var error = Record.Exception(() => Reader(options).Read(file, password, storedKey, CancellationToken.None).Dispose());
        var failure = Assert.IsType<KdbxException>(error);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(password, failure.ToString(), StringComparison.Ordinal);
        return failure;
    }

    /// <summary>Offset just after the header end field, i.e. where the header SHA-256 starts.</summary>
    public static int HeaderLength(byte[] file)
    {
        var position = 12;
        while (true)
        {
            var id = file[position];
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position + 1));
            position += 5 + size;
            if (id == 0)
            {
                return position;
            }
        }
    }

    public static byte[] HeaderField(byte[] file, byte wanted)
    {
        var position = 12;
        while (true)
        {
            var id = file[position];
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position + 1));
            if (id == wanted)
            {
                return file.AsSpan(position + 5, size).ToArray();
            }
            if (id == 0)
            {
                throw new InvalidOperationException("Header field not found.");
            }
            position += 5 + size;
        }
    }

    /// <summary>
    /// Replaces one header field and, unless told otherwise, recomputes the unkeyed header SHA-256 so the
    /// file passes the corruption check and reaches the interpretation of that field. The HMAC stays stale.
    /// </summary>
    public static byte[] ReplaceHeaderField(byte[] file, byte wanted, byte[] data, bool fixHash = true)
    {
        using var header = new MemoryStream();
        header.Write(file, 0, 12);
        var position = 12;
        while (true)
        {
            var id = file[position];
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position + 1));
            var value = id == wanted ? data : file.AsSpan(position + 5, size).ToArray();
            header.WriteByte(id);
            header.Write(BitConverter.GetBytes(value.Length));
            header.Write(value);
            position += 5 + size;
            if (id == 0)
            {
                break;
            }
        }
        var rebuilt = header.ToArray();
        var hash = fixHash ? SHA256.HashData(rebuilt) : file.AsSpan(position, 32).ToArray();
        return [.. rebuilt, .. hash, .. file.AsSpan(position + 32)];
    }

    /// <summary>Re-encodes the KDF variant dictionary with one value replaced (type preserved).</summary>
    public static byte[] WithKdfValue(byte[] file, string key, byte[] value)
    {
        var dictionary = HeaderField(file, 11);
        using var output = new MemoryStream();
        output.Write(dictionary, 0, 2);
        var position = 2;
        while (dictionary[position] != 0)
        {
            var type = dictionary[position];
            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(dictionary.AsSpan(position + 1));
            var name = Encoding.UTF8.GetString(dictionary, position + 5, keyLength);
            var valueLength = BinaryPrimitives.ReadInt32LittleEndian(dictionary.AsSpan(position + 5 + keyLength));
            var current = dictionary.AsSpan(position + 9 + keyLength, valueLength).ToArray();
            var written = name == key ? value : current;
            output.WriteByte(type);
            output.Write(BitConverter.GetBytes(keyLength));
            output.Write(Encoding.UTF8.GetBytes(name));
            output.Write(BitConverter.GetBytes(written.Length));
            output.Write(written);
            position += 9 + keyLength + valueLength;
        }
        output.WriteByte(0);
        return ReplaceHeaderField(file, 11, output.ToArray());
    }
}
