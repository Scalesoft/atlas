using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Kdbx;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class KdbxReaderTests
{
    private const string Rich = "argon2d-aes-gzip.kdbx";

    [Theory]
    [InlineData("argon2d-aes-gzip.kdbx")]
    [InlineData("argon2id-chacha20-none.kdbx")]
    [InlineData("aeskdf-aes-none.kdbx")]
    [InlineData("aeskdf-chacha20-gzip.kdbx")]
    [InlineData("big-notes.kdbx")]
    [InlineData("changed-key.kdbx")]
    public void KeePass_fixture_reads_exactly_what_KeePass_reads(string name)
    {
        var fixture = Fixture(name);
        using var database = Reader().Read(Read(name), Password(name), false, CancellationToken.None);

        Assert.Equal(Uuid(fixture.GetProperty("rootGroupUuid").GetString()!), database.RootGroupUuid);
        // KeePass reports an all-zero UUID while the recycle bin has not been created yet.
        var recycleBin = fixture.GetProperty("recycleBinUuid").GetString();
        Assert.Equal(recycleBin is null || Uuid(recycleBin) == Guid.Empty ? null : Uuid(recycleBin), database.RecycleBinUuid);
        var expected = fixture.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(expected.Count, database.Entries.Count);
        foreach (var item in expected)
        {
            var entry = database.Entries.Single(e => e.Uuid == Uuid(item.GetProperty("uuid").GetString()!));
            Assert.Equal(item.GetProperty("title").GetString(), entry.Title);
            Assert.Equal(item.GetProperty("groupPath").EnumerateArray().Select(p => p.GetString()!), entry.GroupPath);
            Assert.Equal(item.GetProperty("userName").GetString(), entry.UserName.Reveal());
            Assert.Equal(item.GetProperty("password").GetString(), entry.Password.Reveal());
            Assert.Equal(item.GetProperty("url").GetString(), entry.Url.Reveal());
            Assert.Equal(item.GetProperty("notesLength").GetInt32(), entry.Notes.Reveal().Length);
            Assert.Equal(item.GetProperty("notesSha256").GetString(), Convert.ToHexString(SHA256.HashData(entry.Notes.Utf8)));
            Assert.Equal(item.GetProperty("inRecycleBin").GetBoolean(), entry.InRecycleBin);
            Assert.Equal(DateTime.Parse(item.GetProperty("lastModificationTime").GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal), entry.LastModificationTime);
            Assert.Equal(DateTimeKind.Utc, entry.LastModificationTime.Kind);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Fixtures_cover_every_supported_algorithm_combination_and_both_minor_versions()
    {
        var readable = Fixtures.Where(f => f.TryGetProperty("entries", out _)).ToList();
        Assert.Equal(["aeskdf", "argon2d", "argon2id"], readable.Select(f => f.GetProperty("kdf").GetString()).Distinct().Order());
        Assert.Equal(["aes", "chacha20"], readable.Select(f => f.GetProperty("cipher").GetString()).Distinct().Order());
        Assert.Equal(["gzip", "none"], readable.Select(f => f.GetProperty("compression").GetString()).Distinct().Order());
        Assert.Equal(["00040000", "00040001"], readable.Select(f => f.GetProperty("version").GetString()).Distinct().Order());
        Assert.StartsWith("KeePass 2.", Generator, StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_values_inside_skipped_history_keep_the_inner_stream_aligned()
    {
        using var database = Reader().Read(Read(Rich), Password(Rich), false, CancellationToken.None);
        // GitHub has three history items with protected passwords; Mail follows it in the document.
        var github = database.Entries.Single(e => e.Title == "GitHub");
        Assert.Equal("gh-current-7Q", github.Password.Reveal());
        Assert.Equal("mail-after-history", database.Entries.Single(e => e.Title == "Mail").Password.Reveal());
        var router = database.Entries.Single(e => e.Title == "Router");
        Assert.Equal("protected notes value", router.Notes.Reveal());
        Assert.Equal(["Servers", "Linux"], router.GroupPath);
    }

    [Fact]
    public void Recycle_bin_entries_are_flagged_and_history_items_are_not_entries()
    {
        using var database = Reader().Read(Read(Rich), Password(Rich), false, CancellationToken.None);
        Assert.Equal(Uuid(Fixture(Rich).GetProperty("recycleBinUuid").GetString()!), database.RecycleBinUuid);
        var deleted = Assert.Single(database.Entries, e => e.InRecycleBin);
        Assert.Equal("Old VPN", deleted.Title);
        Assert.DoesNotContain(database.Entries, e => e.Password.Reveal() is "gh-oldest-1" or "gh-older-2" or "gh-old-3" or "router-old");
        Assert.Equal("argon2d-aes-gzip", database.DatabaseName);
    }

    [Fact]
    public void Disposal_zeroes_values_and_nothing_prints_secrets()
    {
        var database = Reader().Read(Read(Rich), Password(Rich), false, CancellationToken.None);
        var entry = database.Entries.Single(e => e.Title == "GitHub");
        Assert.DoesNotContain("gh-current", entry.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("gh-current", entry.Password.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub", database.ToString(), StringComparison.Ordinal);
        database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => entry.Password.Reveal());
    }

    [Fact]
    public void Wrong_password_is_reported_with_key_file_guidance()
    {
        var failure = ReadFails(Read(Rich), "not the password");
        Assert.Equal(KdbxFailure.WrongKey, failure.Failure);
        Assert.Contains("key file", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_password_rejected_after_master_key_change_reports_key_changed()
    {
        var file = Read("changed-key.kdbx");
        var previous = Fixture("changed-key.kdbx").GetProperty("previousPassword").GetString()!;
        Assert.Equal(KdbxFailure.KeyChanged, ReadFails(file, previous, storedKey: true).Failure);
        Assert.Equal(KdbxFailure.WrongKey, ReadFails(file, previous, storedKey: false).Failure);
    }

    [Fact]
    public void Password_plus_key_file_database_fails_closed_as_wrong_key()
    {
        Assert.Equal(KdbxFailure.WrongKey, ReadFails(Read("keyfile.kdbx"), Password("keyfile.kdbx")).Failure);
    }

    [Fact]
    public void Kdbx31_is_unsupported_with_upgrade_guidance()
    {
        var failure = ReadFails(Read("kdbx31.kdbx"), Password("kdbx31.kdbx"));
        Assert.Equal(KdbxFailure.Unsupported, failure.Failure);
        Assert.Contains("KDBX 3.1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Argon2", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(100)]
    [InlineData(-1)]
    [InlineData(-40)]
    [InlineData(-2000)]
    public void Truncated_file_is_corrupt(int length)
    {
        var file = Read(Rich);
        var truncated = file[..(length >= 0 ? length : file.Length + length)];
        Assert.Equal(KdbxFailure.Corrupt, ReadFails(truncated, Password(Rich)).Failure);
    }

    [Fact]
    public void Truncation_at_the_header_boundary_is_corrupt()
    {
        var file = Read(Rich);
        var header = HeaderLength(file);
        foreach (var length in new[] { header - 1, header, header + 32, header + 64, header + 64 + 36 })
        {
            Assert.Equal(KdbxFailure.Corrupt, ReadFails(file[..length], Password(Rich)).Failure);
        }
    }

    [Fact]
    public void Decompression_bomb_stops_at_the_limit_with_bounded_allocation()
    {
        var file = Read("bomb.kdbx");
        Assert.True(file.Length < 200_000);
        var reader = Reader();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Record.Exception(() => reader.Read(file, Password("bomb.kdbx"), false, CancellationToken.None));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(KdbxFailure.LimitExceeded, Assert.IsType<KdbxException>(error).Failure);
        Assert.Contains(nameof(VaultOptions.MaxDecompressedBytes), error.Message, StringComparison.Ordinal);
        // 64 MiB were decompressed before the limit; the reader streamed them (Argon2 itself takes 1 MiB).
        Assert.True(allocated < 8L * 1024 * 1024, $"Allocated {allocated} bytes.");
    }

    [Fact]
    public void Reading_large_notes_allocates_a_bounded_multiple_of_the_content()
    {
        var file = Read("big-notes.kdbx");
        var reader = Reader();
        var before = GC.GetAllocatedBytesForCurrentThread();
        using (reader.Read(file, Password("big-notes.kdbx"), false, CancellationToken.None))
        {
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Two 700 KiB notes (current and history) in a 1 MiB file.
        Assert.True(allocated < 32L * 1024 * 1024, $"Allocated {allocated} bytes.");
    }

    [Fact]
    public void Group_nesting_beyond_the_limit_is_rejected()
    {
        var failure = ReadFails(Read("deep-groups.kdbx"), Password("deep-groups.kdbx"));
        Assert.Equal(KdbxFailure.LimitExceeded, failure.Failure);
        Assert.Contains(nameof(VaultOptions.MaxGroupDepth), failure.Message, StringComparison.Ordinal);
        using var database = Reader(new VaultOptions { MaxGroupDepth = 71 }).Read(Read("deep-groups.kdbx"), Password("deep-groups.kdbx"), false, CancellationToken.None);
        Assert.Equal(70, Assert.Single(database.Entries).GroupPath.Count);
    }

    public static TheoryData<string, string, long> OversizedKdf => new()
    {
        { "argon2id-chacha20-none.kdbx", "M", 512L * 1024 * 1024 },
        { "argon2id-chacha20-none.kdbx", "I", 33 },
        { "argon2d-aes-gzip.kdbx", "M", long.MaxValue & ~1023L },
        { "argon2d-aes-gzip.kdbx", "I", long.MaxValue },
        { "aeskdf-aes-none.kdbx", "R", 60_000_001 },
        { "aeskdf-chacha20-gzip.kdbx", "R", long.MaxValue },
    };

    [Theory]
    [MemberData(nameof(OversizedKdf))]
    public void Oversized_kdf_parameters_are_rejected_before_derivation(string name, string key, long value)
    {
        var file = WithKdfValue(Read(name), key, BitConverter.GetBytes((ulong)value));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var failure = ReadFails(file, Password(name));
        Assert.Equal(KdbxFailure.LimitExceeded, failure.Failure);
        // No Argon2 memory and no AES rounds: the rejection happened while parsing the header.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void Oversized_parallelism_is_rejected_before_derivation()
    {
        var file = WithKdfValue(Read("argon2id-chacha20-none.kdbx"), "P", BitConverter.GetBytes(9u));
        Assert.Equal(KdbxFailure.LimitExceeded, ReadFails(file, Password("argon2id-chacha20-none.kdbx")).Failure);
    }

    [Fact]
    public void Header_change_without_matching_sha256_is_corrupt_not_a_limit()
    {
        var file = ReplaceHeaderField(Read(Rich), 3, BitConverter.GetBytes(7u), fixHash: false);
        Assert.Equal(KdbxFailure.Corrupt, ReadFails(file, Password(Rich)).Failure);
    }

    [Theory]
    [InlineData("AD68F29F576F4BB9A36AD47AF965346C", "Twofish")]
    [InlineData("000102030405060708090A0B0C0D0E0F", "unknown encryption")]
    public void Unsupported_cipher_is_reported_with_guidance(string cipher, string expected)
    {
        var file = ReplaceHeaderField(Read(Rich), 2, Convert.FromHexString(cipher));
        var failure = ReadFails(file, Password(Rich));
        Assert.Equal(KdbxFailure.Unsupported, failure.Failure);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        Assert.Contains("AES-256 or ChaCha20", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_compression_and_kdf_are_unsupported()
    {
        var compression = ReplaceHeaderField(Read(Rich), 3, BitConverter.GetBytes(2u));
        Assert.Equal(KdbxFailure.Unsupported, ReadFails(compression, Password(Rich)).Failure);
        var kdf = WithKdfValue(Read(Rich), "$UUID", new byte[16]);
        Assert.Equal(KdbxFailure.Unsupported, ReadFails(kdf, Password(Rich)).Failure);
    }

    [Fact]
    public void KeePass1_and_foreign_files_are_rejected()
    {
        var keePass1 = Read(Rich);
        BinaryPrimitives.WriteUInt32LittleEndian(keePass1.AsSpan(4), 0xB54BFB65);
        Assert.Equal(KdbxFailure.Unsupported, ReadFails(keePass1, "unused password").Failure);
        var newer = Read(Rich);
        BinaryPrimitives.WriteUInt32LittleEndian(newer.AsSpan(8), 0x00050000);
        Assert.Equal(KdbxFailure.Unsupported, ReadFails(newer, "unused password").Failure);
        Assert.Equal(KdbxFailure.Corrupt, ReadFails(Encoding.UTF8.GetBytes("<KeePassFile></KeePassFile>"), "unused password").Failure);
    }

    public static TheoryData<string, Func<VaultOptions>, string> ConfiguredLimits => new()
    {
        { Rich, () => new VaultOptions { MaxFileBytes = 1024 }, nameof(VaultOptions.MaxFileBytes) },
        { Rich, () => new VaultOptions { MaxHeaderBytes = 100 }, nameof(VaultOptions.MaxHeaderBytes) },
        { Rich, () => new VaultOptions { MaxHeaderFieldBytes = 16 }, nameof(VaultOptions.MaxHeaderFieldBytes) },
        { Rich, () => new VaultOptions { MaxEntries = 3 }, nameof(VaultOptions.MaxEntries) },
        { Rich, () => new VaultOptions { MaxGroupDepth = 2 }, nameof(VaultOptions.MaxGroupDepth) },
        { "argon2id-chacha20-none.kdbx", () => new VaultOptions { MaxDecompressedBytes = 4096 }, nameof(VaultOptions.MaxDecompressedBytes) },
        { "argon2id-chacha20-none.kdbx", () => new VaultOptions { MaxArgon2MemoryBytes = 512 * 1024 }, nameof(VaultOptions.MaxArgon2MemoryBytes) },
        { "argon2id-chacha20-none.kdbx", () => new VaultOptions { MaxArgon2Iterations = 1 }, nameof(VaultOptions.MaxArgon2Iterations) },
        { "argon2id-chacha20-none.kdbx", () => new VaultOptions { MaxArgon2Parallelism = 1 }, nameof(VaultOptions.MaxArgon2Parallelism) },
        { "aeskdf-aes-none.kdbx", () => new VaultOptions { MaxAesKdfRounds = 1999 }, nameof(VaultOptions.MaxAesKdfRounds) },
        { "big-notes.kdbx", () => new VaultOptions { MaxXmlFieldBytes = 64 * 1024 }, nameof(VaultOptions.MaxXmlFieldBytes) },
    };

    [Theory]
    [MemberData(nameof(ConfiguredLimits))]
    public void Every_configured_limit_is_enforced_with_its_option_name(string name, Func<VaultOptions> options, string option)
    {
        var failure = ReadFails(Read(name), Password(name), options());
        Assert.Equal(KdbxFailure.LimitExceeded, failure.Failure);
        Assert.Contains($"Vaults:{option}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_limit_failure_does_not_leak_field_content()
    {
        var failure = ReadFails(Read("big-notes.kdbx"), Password("big-notes.kdbx"), new VaultOptions { MaxXmlFieldBytes = 1024 });
        Assert.DoesNotContain("big-notes-pw", failure.ToString(), StringComparison.Ordinal);
        Assert.True(failure.Message.Length < 200);
    }

    [Fact]
    public void Cancelled_read_stops_before_key_derivation()
    {
        Assert.Throws<OperationCanceledException>(() => Reader().Read(Read(Rich), Password(Rich), false, new CancellationToken(true)));
    }

    [Fact]
    public async Task Key_derivation_is_single_flight()
    {
        var derivation = new KdbxKeyDerivation();
        var reader = Reader(keyDerivation: derivation);
        // Occupy the derivation with a long AES-KDF (about a second); its wrong HMAC does not matter.
        var slow = WithKdfValue(Read("aeskdf-aes-none.kdbx"), "R", BitConverter.GetBytes(40_000_000UL));
        using var stopSlow = new CancellationTokenSource();
        var slowToken = stopSlow.Token;
        var running = Task.Run(() => Record.Exception(() => reader.Read(slow, "x", false, slowToken)));
        Exception? slowFailure;
        try
        {
            // Task.Run may still be queued under suite load; wait until it owns the derivation gate.
            using var startedLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (derivation.Derivations == 0 && !running.IsCompleted)
            {
                await Task.Delay(10, startedLimit.Token);
            }
            Assert.False(running.IsCompleted);

            // Argon2 has no internal cancellation, so only a caller waiting for the gate can observe this token.
            using var waitLimit = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.Throws<OperationCanceledException>(() => reader.Read(Read(Rich), Password(Rich), false, waitLimit.Token));
        }
        finally
        {
            await stopSlow.CancelAsync();
            slowFailure = await running;
        }
        Assert.IsType<OperationCanceledException>(slowFailure);
        using var database = reader.Read(Read(Rich), Password(Rich), false, CancellationToken.None);
        Assert.NotEmpty(database.Entries);
    }

    [Fact]
    public void Vault_options_bind_from_configuration_and_reject_nonsense()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vaults:MaxEntries"] = "10",
            ["Vaults:MaxArgon2MemoryBytes"] = "1048576",
        }).Build();
        var options = VaultOptions.Read(configuration);
        Assert.Equal(10, options.MaxEntries);
        Assert.Equal(1048576, options.MaxArgon2MemoryBytes);
        Assert.Equal(16L * 1024 * 1024, options.MaxFileBytes);
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Vaults:MaxGroupDepth"] = "0" }).Build();
        Assert.Throws<InvalidOperationException>(() => VaultOptions.Read(invalid));
    }
}
