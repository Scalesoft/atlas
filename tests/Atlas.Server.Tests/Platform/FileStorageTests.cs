using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Platform.Storage;
using Atlas.Server.Tests.Backups;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class FileStorageTests : IDisposable
{
    private readonly string m_directory = Path.Combine(Path.GetTempPath(), "atlas-storage-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(200_000)]
    public async Task Local_bounded_read_accepts_the_exact_limit_and_rejects_one_byte_more(int size)
    {
        using var storage = OpenLocal();
        var bytes = RandomNumberGenerator.GetBytes(size);
        await storage.PublishNewAsync("vault.kdbx", new MemoryStream(bytes), CancellationToken.None);
        Assert.Equal(bytes, await storage.ReadBoundedAsync("vault.kdbx", size, CancellationToken.None));
        if (size > 0)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadBoundedAsync("vault.kdbx", size - 1, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Bounded_read_validates_limit_and_name_before_touching_storage()
    {
        using var storage = OpenLocal();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.ReadBoundedAsync("vault.kdbx", -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.ReadBoundedAsync("vault.kdbx", long.MaxValue, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadBoundedAsync("../vault.kdbx", 10, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.GetMetadataAsync("a/b", CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ReadBoundedAsync("vault.kdbx", 10, new CancellationToken(true)));
    }

    [Fact]
    public async Task Local_listing_returns_matching_files_of_the_folder_itself_in_name_order_and_bounded()
    {
        using var storage = OpenLocal();
        foreach (var name in new[] { "b.kdbx", "A.KDBX", "c.kdbx", "notes.txt", "d.kdbx.bak" })
        {
            await storage.PublishNewAsync(name, new MemoryStream(new byte[3]), CancellationToken.None);
        }
        Directory.CreateDirectory(Path.Combine(m_directory, "nested.kdbx"));
        Directory.CreateDirectory(Path.Combine(m_directory, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(m_directory, "sub", "deep.kdbx"), new byte[1]);

        var all = await storage.ListAsync(".kdbx", 10, CancellationToken.None);
        Assert.Equal(["A.KDBX", "b.kdbx", "c.kdbx"], all.Files.Select(file => file.Name));
        Assert.False(all.Truncated);
        Assert.All(all.Files, file => Assert.Equal(3, file.Length));
        Assert.All(all.Files, file => Assert.Equal(DateTimeKind.Utc, file.LastWriteUtc.Kind));

        var bounded = await storage.ListAsync(".kdbx", 2, CancellationToken.None);
        Assert.Equal(["A.KDBX", "b.kdbx"], bounded.Files.Select(file => file.Name));
        Assert.True(bounded.Truncated);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.ListAsync(".kdbx", 0, CancellationToken.None));
        using var missing = FileStorages.Open(new FileStorageOptions(Path.Combine(m_directory, "missing"), "", "", null));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => missing.ListAsync(".kdbx", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Local_metadata_reports_length_and_write_time_and_tracks_changes()
    {
        using var storage = OpenLocal();
        await storage.PublishNewAsync("vault.kdbx", new MemoryStream(new byte[10]), CancellationToken.None);
        var path = Path.Combine(m_directory, "vault.kdbx");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var first = await storage.GetMetadataAsync("vault.kdbx", CancellationToken.None);
        Assert.Equal(10, first.Length);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), first.LastWriteUtc);
        Assert.Equal(DateTimeKind.Utc, first.LastWriteUtc.Kind);

        await File.WriteAllBytesAsync(path, new byte[12]);
        var second = await storage.GetMetadataAsync("vault.kdbx", CancellationToken.None);
        Assert.Equal(12, second.Length);
        Assert.NotEqual(first, second);
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.GetMetadataAsync("missing.kdbx", CancellationToken.None));
    }

    [Fact]
    public async Task Open_reader_does_not_block_an_in_place_save_or_an_atomic_replace()
    {
        using var storage = OpenLocal();
        await storage.PublishNewAsync("vault.kdbx", new MemoryStream(Encoding.UTF8.GetBytes("version 1")), CancellationToken.None);
        var path = Path.Combine(m_directory, "vault.kdbx");
        await using var reader = await storage.OpenReadAsync("vault.kdbx", CancellationToken.None);

        // KeePass either rewrites the file in place or writes a temporary file, deletes the original and
        // renames the temporary file. Both need the Write and Delete sharing of the reader.
        await using (var inPlace = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            await inPlace.WriteAsync(Encoding.UTF8.GetBytes("version 2"));
        }
        var temporary = Path.Combine(m_directory, "vault.kdbx.tmp");
        await File.WriteAllTextAsync(temporary, "version 3");
        File.Delete(path);
        File.Move(temporary, path);

        Assert.Equal("version 3", Encoding.UTF8.GetString(await storage.ReadBoundedAsync("vault.kdbx", 100, CancellationToken.None)));
    }

    [Fact]
    public async Task Publish_new_never_replaces_an_existing_file()
    {
        using var storage = OpenLocal();
        await storage.PublishNewAsync("vault.kdbx", new MemoryStream([1, 2, 3]), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => storage.PublishNewAsync("vault.kdbx", new MemoryStream([9]), CancellationToken.None));
        Assert.Equal([1, 2, 3], await storage.ReadBoundedAsync("vault.kdbx", 3, CancellationToken.None));
        Assert.Single(Directory.GetFiles(m_directory));
    }

    [BackupDestinationTests.SmbFact]
    public async Task Smb_bounded_read_and_metadata_match_the_published_file()
    {
        using var storage = FileStorages.Open(new FileStorageOptions(
            Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_DESTINATION")!,
            Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_USERNAME") ?? "",
            Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_DOMAIN") ?? "",
            Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_PASSWORD")));
        var name = $"atlas-vault-{Guid.NewGuid():N}.kdbx";
        var bytes = RandomNumberGenerator.GetBytes(150_000);
        await storage.PublishNewAsync(name, new MemoryStream(bytes), CancellationToken.None);
        try
        {
            Assert.Equal(bytes, await storage.ReadBoundedAsync(name, bytes.Length, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadBoundedAsync(name, bytes.Length - 1, CancellationToken.None));
            var metadata = await storage.GetMetadataAsync(name, CancellationToken.None);
            Assert.Equal(bytes.Length, metadata.Length);
            Assert.Equal(DateTimeKind.Utc, metadata.LastWriteUtc.Kind);
            Assert.InRange(metadata.LastWriteUtc, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        }
        finally
        {
            await storage.DeleteAsync(name, CancellationToken.None);
        }
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.GetMetadataAsync(name, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(m_directory))
        {
            Directory.Delete(m_directory, true);
        }
    }

    private IFileStorage OpenLocal() => FileStorages.Open(new FileStorageOptions(m_directory, "", "", null));
}
