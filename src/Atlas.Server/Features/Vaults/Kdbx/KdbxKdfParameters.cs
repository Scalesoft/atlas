using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Key derivation parameters from the outer header. <see cref="Parse"/> rejects unknown functions, malformed
/// values and any cost above the configured limits, all before a single derivation round runs.
/// </summary>
internal sealed class KdbxKdfParameters
{
    private const uint Argon2Version10 = 0x10;
    private const uint Argon2Version13 = 0x13;
    private const int MinimumSaltBytes = 8;

    public required KdbxKdfKind Kind { get; init; }

    /// <summary>Argon2 salt or AES-KDF seed (the AES key).</summary>
    public required byte[] Salt { get; init; }

    public ulong Rounds { get; init; }

    public ulong MemoryBytes { get; init; }

    public ulong Iterations { get; init; }

    public uint Parallelism { get; init; }

    public uint Version { get; init; }

    public byte[]? SecretKey { get; init; }

    public byte[]? AssociatedData { get; init; }

    /// <summary>
    /// SHA-256 over every input of the derivation except the key: function, version, cost values, salt or seed, secret and
    /// associated data, each length-prefixed. Equal identities derive equal transformed keys from one composite key.
    /// </summary>
    public byte[] Identity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var number = new byte[8];
        void Append(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(number, value);
            hash.AppendData(number);
        }
        void AppendBytes(byte[]? value)
        {
            // Missing and empty derive the same key: neither reaches Argon2.
            Append((ulong)(value?.Length ?? 0));
            hash.AppendData(value ?? []);
        }
        Append((ulong)Kind);
        Append(Version);
        Append(Rounds);
        Append(MemoryBytes);
        Append(Iterations);
        Append(Parallelism);
        AppendBytes(Salt);
        AppendBytes(SecretKey);
        AppendBytes(AssociatedData);
        return hash.GetHashAndReset();
    }

    public static KdbxKdfParameters Parse(ReadOnlySpan<byte> data, VaultOptions options)
    {
        var dictionary = KdbxVariantDictionary.Parse(data);
        var uuid = dictionary.Bytes("$UUID") ?? throw KdbxException.Corrupt();
        if (uuid.AsSpan().SequenceEqual(KdbxFormat.AesKdf))
        {
            var seed = dictionary.Bytes("S");
            var rounds = dictionary.UInt64("R") ?? throw KdbxException.Corrupt();
            if (seed is not { Length: 32 })
            {
                throw KdbxException.Corrupt();
            }
            if (rounds > (ulong)options.MaxAesKdfRounds)
            {
                throw KdbxException.Limit("AES-KDF rounds", nameof(VaultOptions.MaxAesKdfRounds));
            }
            return new KdbxKdfParameters { Kind = KdbxKdfKind.AesKdf, Salt = seed, Rounds = rounds };
        }

        KdbxKdfKind kind;
        if (uuid.AsSpan().SequenceEqual(KdbxFormat.Argon2dKdf))
        {
            kind = KdbxKdfKind.Argon2d;
        }
        else if (uuid.AsSpan().SequenceEqual(KdbxFormat.Argon2idKdf))
        {
            kind = KdbxKdfKind.Argon2id;
        }
        else
        {
            throw KdbxException.Unsupported("The database uses an unknown key derivation function. In KeePass choose Argon2d, Argon2id or AES-KDF under Database Settings, Security, and save the database.");
        }
        var salt = dictionary.Bytes("S") ?? throw KdbxException.Corrupt();
        var parallelism = dictionary.UInt32("P") ?? throw KdbxException.Corrupt();
        var memory = dictionary.UInt64("M") ?? throw KdbxException.Corrupt();
        var iterations = dictionary.UInt64("I") ?? throw KdbxException.Corrupt();
        var version = dictionary.UInt32("V") ?? throw KdbxException.Corrupt();
        if (version is not (Argon2Version10 or Argon2Version13))
        {
            throw KdbxException.Unsupported("The database uses an unknown Argon2 version. Save it with a current KeePass version.");
        }
        if (salt.Length < MinimumSaltBytes || parallelism == 0 || iterations == 0 || memory % 1024 != 0)
        {
            throw KdbxException.Corrupt();
        }
        if (memory > (ulong)options.MaxArgon2MemoryBytes)
        {
            throw KdbxException.Limit("Argon2 memory", nameof(VaultOptions.MaxArgon2MemoryBytes));
        }
        if (iterations > (ulong)options.MaxArgon2Iterations)
        {
            throw KdbxException.Limit("Argon2 iterations", nameof(VaultOptions.MaxArgon2Iterations));
        }
        if (parallelism > (uint)options.MaxArgon2Parallelism)
        {
            throw KdbxException.Limit("Argon2 parallelism", nameof(VaultOptions.MaxArgon2Parallelism));
        }
        // Argon2 requires at least eight 1 KiB blocks per lane.
        if (memory / 1024 < 8UL * parallelism)
        {
            throw KdbxException.Corrupt();
        }
        return new KdbxKdfParameters
        {
            Kind = kind,
            Salt = salt,
            MemoryBytes = memory,
            Iterations = iterations,
            Parallelism = parallelism,
            Version = version,
            SecretKey = dictionary.Bytes("K"),
            AssociatedData = dictionary.Bytes("A"),
        };
    }

    public static KdbxKdfParameters Argon2id(byte[] salt, ulong memoryBytes, ulong iterations, uint parallelism) => new()
    {
        Kind = KdbxKdfKind.Argon2id,
        Salt = salt,
        MemoryBytes = memoryBytes,
        Iterations = iterations,
        Parallelism = parallelism,
        Version = Argon2Version13,
    };

    /// <summary>Encodes Argon2 parameters for a new database header.</summary>
    public byte[] ToArgon2Dictionary()
    {
        var dictionary = new KdbxVariantDictionary();
        dictionary.Set("$UUID", (Kind == KdbxKdfKind.Argon2d ? KdbxFormat.Argon2dKdf : KdbxFormat.Argon2idKdf).ToArray());
        dictionary.Set("S", Salt);
        dictionary.Set("P", Parallelism);
        dictionary.Set("M", MemoryBytes);
        dictionary.Set("I", Iterations);
        dictionary.Set("V", Version);
        return dictionary.ToArray();
    }
}
