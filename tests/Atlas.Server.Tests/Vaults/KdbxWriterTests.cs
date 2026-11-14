using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Kdbx;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class KdbxWriterTests
{
    private static readonly KdbxNewDatabaseSettings Cheap = new() { Argon2MemoryBytes = 1024 * 1024, Argon2Iterations = 1, Argon2Parallelism = 2 };

    [Fact]
    public void New_database_reads_back_with_root_group_and_recycle_bin()
    {
        const string password = "Nové heslo ✓";
        var file = Writer().CreateEmpty(password, "Provozní trezor", Cheap, CancellationToken.None);
        using var database = Reader().Read(file, password, false, CancellationToken.None);
        Assert.Equal("Provozní trezor", database.DatabaseName);
        Assert.NotEqual(Guid.Empty, database.RootGroupUuid);
        Assert.NotNull(database.RecycleBinUuid);
        Assert.NotEqual(database.RootGroupUuid, database.RecycleBinUuid);
        Assert.Empty(database.Entries);
        Assert.Equal(KdbxFailure.WrongKey, ReadFails(file, "other").Failure);
    }

    [Fact]
    public void Default_settings_write_kdbx4_with_argon2id_64mib_3_2_chacha20_and_gzip()
    {
        var file = Writer().CreateEmpty("default settings", "Vault", new KdbxNewDatabaseSettings(), CancellationToken.None);
        Assert.Equal(0x00040000u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal("D6038A2B8B6F4CB5A524339A31DBB59A", Convert.ToHexString(HeaderField(file, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(HeaderField(file, 3)));
        Assert.Equal(12, HeaderField(file, 7).Length);
        var kdf = HeaderField(file, 11);
        var hex = Convert.ToHexString(kdf);
        Assert.Contains("9E298B1956DB4773B23DFC3EC6F0A1E6", hex, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString([.. "M"u8, 8, 0, 0, 0, .. BitConverter.GetBytes(64UL * 1024 * 1024)]), hex, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString([.. "I"u8, 8, 0, 0, 0, .. BitConverter.GetBytes(3UL)]), hex, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString([.. "P"u8, 4, 0, 0, 0, .. BitConverter.GetBytes(2u)]), hex, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_new_database_has_fresh_keys_and_identifiers()
    {
        var first = Writer().CreateEmpty("same", "Same", Cheap, CancellationToken.None);
        var second = Writer().CreateEmpty("same", "Same", Cheap, CancellationToken.None);
        Assert.NotEqual(HeaderField(first, 4), HeaderField(second, 4));
        using var a = Reader().Read(first, "same", false, CancellationToken.None);
        using var b = Reader().Read(second, "same", false, CancellationToken.None);
        Assert.NotEqual(a.RootGroupUuid, b.RootGroupUuid);
    }

    [Fact]
    public void Invalid_arguments_are_rejected_before_any_work()
    {
        Assert.Throws<ArgumentException>(() => Writer().CreateEmpty("", "Vault", Cheap, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => Writer().CreateEmpty("x", "Vault", Cheap with { Argon2MemoryBytes = 512L * 1024 * 1024 }, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => Writer().CreateEmpty("x", "Vault", Cheap with { Argon2MemoryBytes = 1000 }, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => Writer().CreateEmpty("x", "Vault", Cheap with { Argon2Parallelism = 0 }, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => Writer().CreateEmpty("x", "Vault", Cheap, new CancellationToken(true)));
    }

    [KeePassFact]
    public async Task KeePass_opens_the_new_database()
    {
        const string password = "KeePass round trip ✓";
        var file = Writer().CreateEmpty(password, "Round trip", new KdbxNewDatabaseSettings(), CancellationToken.None);
        using var database = Reader().Read(file, password, false, CancellationToken.None);
        var path = Path.Combine(Path.GetTempPath(), $"atlas-kdbx-{Guid.NewGuid():N}.kdbx");
        await File.WriteAllBytesAsync(path, file);
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Folder, "verify-with-keepass.ps1"), "-Path", path, "-KeePass", KeePassFactAttribute.KeePassPath })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["ATLAS_KDBX_PASSWORD"] = password;
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            var keePass = JsonDocument.Parse(output).RootElement;
            Assert.Equal("Round trip", keePass.GetProperty("name").GetString());
            Assert.Equal(Hex(database.RootGroupUuid), keePass.GetProperty("rootGroupUuid").GetString());
            Assert.Equal("Round trip", keePass.GetProperty("rootGroupName").GetString());
            Assert.True(keePass.GetProperty("recycleBinEnabled").GetBoolean());
            Assert.Equal(Hex(database.RecycleBinUuid!.Value), keePass.GetProperty("recycleBinUuid").GetString());
            Assert.Equal(1, keePass.GetProperty("groups").GetInt32());
            Assert.Equal(0, keePass.GetProperty("entries").GetInt32());
            Assert.Equal("9E298B1956DB4773B23DFC3EC6F0A1E6", keePass.GetProperty("kdf").GetString());
            Assert.Equal("D6038A2B8B6F4CB5A524339A31DBB59A", keePass.GetProperty("cipher").GetString());
            Assert.Equal("GZip", keePass.GetProperty("compression").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static KdbxWriter Writer()
    {
        var options = new VaultOptions();
        var derivation = new KdbxKeyDerivation();
        return new KdbxWriter(options, derivation, new KdbxReader(options, derivation));
    }

    public sealed class KeePassFactAttribute : FactAttribute
    {
        public static string KeePassPath => Environment.GetEnvironmentVariable("ATLAS_TEST_KEEPASS")
            ?? @"C:\Program Files\KeePass Password Safe 2\KeePass.exe";

        public KeePassFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(KeePassPath))
            {
                Skip = "Install KeePass 2 (or set ATLAS_TEST_KEEPASS to KeePass.exe) to verify new databases with KeePass itself.";
            }
        }
    }
}
