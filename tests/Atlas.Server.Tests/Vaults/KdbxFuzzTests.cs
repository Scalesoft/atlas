using System.Buffers.Binary;
using Atlas.Server.Features.Vaults.Kdbx;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// Deterministic mutation of real KeePass files. Every byte is covered by the header SHA-256/HMAC or a block
/// HMAC, so every mutation must fail, and it must fail as <see cref="KdbxException"/>: any other exception
/// type means an unchecked path in the parser.
/// </summary>
public sealed class KdbxFuzzTests
{
    private const int Seed = 20260926;

    [Theory]
    [InlineData("argon2id-chacha20-none.kdbx", 150)]
    [InlineData("argon2d-aes-gzip.kdbx", 150)]
    [InlineData("aeskdf-chacha20-gzip.kdbx", 100)]
    public void Bit_flips_fail_only_with_kdbx_exception(string name, int iterations)
    {
        var original = Read(name);
        var header = HeaderLength(original);
        var random = new Random(Seed);
        for (var i = 0; i < iterations; i++)
        {
            var file = (byte[])original.Clone();
            // Alternate between the header (cheap checks) and the payload (key derivation, then HMAC).
            var position = i % 2 == 0 ? random.Next(header + 64) : random.Next(header + 64, file.Length);
            file[position] ^= (byte)(1 << random.Next(8));
            AssertFailsCleanly(file, name, $"bit flip at {position}");
        }
    }

    [Theory]
    [InlineData("argon2id-chacha20-none.kdbx")]
    [InlineData("big-notes.kdbx")]
    public void Truncation_fails_only_with_kdbx_exception(string name)
    {
        var original = Read(name);
        var random = new Random(Seed);
        for (var i = 0; i < 60; i++)
        {
            var length = random.Next(original.Length);
            AssertFailsCleanly(original[..length], name, $"truncation to {length}");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(0x7FFF0000)]
    [InlineData(16 * 1024 + 1)]
    [InlineData(65536)]
    [InlineData(1)]
    public void Inflated_header_field_lengths_fail_only_with_kdbx_exception(int length)
    {
        const string name = "argon2d-aes-gzip.kdbx";
        var original = Read(name);
        var position = 12;
        while (true)
        {
            var file = (byte[])original.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(position + 1), length);
            AssertFailsCleanly(file, name, $"field length {length} at {position}");
            var id = original[position];
            position += 5 + BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(position + 1));
            if (id == 0)
            {
                break;
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    [InlineData(1)]
    public void Inflated_block_lengths_fail_only_with_kdbx_exception(int length)
    {
        const string name = "big-notes.kdbx";
        var original = Read(name);
        var file = (byte[])original.Clone();
        // First block: [HMAC 32][size 4][data] directly after header, SHA-256 and HMAC.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(HeaderLength(original) + 64 + 32), length);
        AssertFailsCleanly(file, name, $"block length {length}");
    }

    [Fact]
    public void Random_garbage_after_a_valid_prefix_fails_only_with_kdbx_exception()
    {
        const string name = "aeskdf-aes-none.kdbx";
        var original = Read(name);
        var random = new Random(Seed);
        for (var i = 0; i < 40; i++)
        {
            var prefix = random.Next(original.Length);
            var file = new byte[original.Length];
            original.AsSpan(0, prefix).CopyTo(file);
            random.NextBytes(file.AsSpan(prefix));
            AssertFailsCleanly(file, name, $"garbage after {prefix}");
        }
    }

    private static void AssertFailsCleanly(byte[] file, string name, string mutation)
    {
        var error = Record.Exception(() => Reader().Read(file, Password(name), false, CancellationToken.None).Dispose());
        Assert.True(error is KdbxException { InnerException: null }, $"{name}, {mutation}: {error?.GetType().Name ?? "no failure"}");
    }
}
