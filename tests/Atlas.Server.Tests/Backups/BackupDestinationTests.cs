using System.Text;
using Atlas.Server.Platform.Storage;
using Xunit;

namespace Atlas.Server.Tests.Backups;

public sealed class BackupDestinationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "atlas-destination-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Local_destination_publishes_roundtrips_and_deletes_only_requested_file()
    {
        using var destination = OpenLocal();
        await destination.TestAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(directory));
        await destination.PublishNewAsync("wiki-first.zip", Content("first archive"), CancellationToken.None);
        await destination.PublishNewAsync("wiki-second.zip", Content("second archive"), CancellationToken.None);
        await using (var read = await destination.OpenReadAsync("wiki-first.zip", CancellationToken.None))
            Assert.Equal("first archive", await new StreamReader(read).ReadToEndAsync());
        await destination.DeleteAsync("wiki-first.zip", CancellationToken.None);
        Assert.Equal(["wiki-second.zip"], Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Interrupted_publish_never_exposes_completed_name_or_leaves_partial_file()
    {
        using var destination = OpenLocal();
        using var content = new InterruptedStream();
        await Assert.ThrowsAsync<IOException>(() => destination.PublishNewAsync("wiki-incomplete.zip", content, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Cancellation_does_not_create_an_archive()
    {
        using var destination = OpenLocal();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destination.PublishNewAsync("wiki-cancelled.zip", Content("cancel"), new CancellationToken(true)));
        Assert.False(File.Exists(Path.Combine(directory, "wiki-cancelled.zip")));
    }

    [Theory]
    [InlineData("../outside.zip")]
    [InlineData("..\\outside.zip")]
    [InlineData("/absolute.zip")]
    [InlineData("stream:alternate")]
    [InlineData("trailing.")]
    [InlineData("CON.zip")]
    [InlineData("a?.zip")]
    [InlineData("")]
    public async Task Unsafe_archive_names_are_rejected_for_all_operations(string name)
    {
        using var destination = OpenLocal();
        await Assert.ThrowsAsync<ArgumentException>(() => destination.PublishNewAsync(name, Content("data"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.OpenReadAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.DeleteAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.ExistsAsync(name, CancellationToken.None));
    }

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("\\\\host\\share")]
    [InlineData("//host/share")]
    [InlineData("smb://host")]
    [InlineData("smb://host/share/../other")]
    [InlineData("smb://host/share/%2e%2e/other")]
    [InlineData("smb://host/share/sub%2ffolder")]
    [InlineData("smb://user:secret@host/share")]
    [InlineData("smb://host:1445/share")]
    [InlineData("smb://host/share?password=secret")]
    [InlineData("smb://host/share#fragment")]
    [InlineData("smb://host/share/folder\\escape")]
    public void Invalid_destination_is_rejected_without_repeating_credentials(string path)
    {
        var error = Assert.Throws<ArgumentException>(() => FileStorages.Open(new(path, "user", "domain", "secret")));
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void Valid_smb_destination_is_parsed_without_mounting_or_connecting()
    {
        using var destination = FileStorages.Open(new("smb://backup-host/archive/wiki%20backups", "user", "domain", "secret"));
        Assert.NotNull(destination);
    }

    [Fact]
    public async Task Local_existence_check_tracks_publish_and_delete_and_observes_cancellation()
    {
        using var destination = OpenLocal();
        await CheckExistenceAsync(destination);
    }

    [SmbFact]
    public async Task Smb_existence_check_tracks_publish_and_delete_and_observes_cancellation()
    {
        using var destination = FileStorages.Open(SmbOptions());
        await CheckExistenceAsync(destination);
    }

    private static async Task CheckExistenceAsync(IFileStorage destination)
    {
        var name = $"atlas-exists-{Guid.NewGuid():N}.zip";
        Assert.False(await destination.ExistsAsync(name, CancellationToken.None));
        try
        {
            using var content = Content("archive data");
            await destination.PublishNewAsync(name, content, CancellationToken.None);
            Assert.True(await destination.ExistsAsync(name, CancellationToken.None));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destination.ExistsAsync(name, new CancellationToken(true)));
        }
        finally
        {
            await destination.DeleteAsync(name, CancellationToken.None);
        }
        Assert.False(await destination.ExistsAsync(name, CancellationToken.None));
    }

    [SmbFact]
    public async Task Direct_smb_roundtrips_multiple_blocks_preserves_existing_archive_and_removes_probe()
    {
        using var destination = FileStorages.Open(SmbOptions());
        await destination.TestAsync(CancellationToken.None);
        var name = $"atlas-test-{Guid.NewGuid():N}.zip";
        var expected = System.Security.Cryptography.RandomNumberGenerator.GetBytes(150000);
        try
        {
            using var content = new MemoryStream(expected);
            await destination.PublishNewAsync(name, content, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => destination.PublishNewAsync(name, Content("replacement"), CancellationToken.None));
            await using var read = await destination.OpenReadAsync(name, CancellationToken.None);
            using var actual = new MemoryStream();
            await read.CopyToAsync(actual);
            Assert.Equal(expected, actual.ToArray());
        }
        finally
        {
            await destination.DeleteAsync(name, CancellationToken.None);
        }
        await Assert.ThrowsAsync<IOException>(() => destination.OpenReadAsync(name, CancellationToken.None));
        await destination.DeleteAsync(name, CancellationToken.None);
    }

    [SmbFact]
    public async Task Smb_interrupted_upload_does_not_publish_an_archive()
    {
        using var destination = FileStorages.Open(SmbOptions());
        var name = $"atlas-interrupted-{Guid.NewGuid():N}.zip";
        using var content = new InterruptedStream();
        await Assert.ThrowsAsync<IOException>(() => destination.PublishNewAsync(name, content, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => destination.OpenReadAsync(name, CancellationToken.None));
    }

    [SmbFact]
    public async Task Smb_authentication_failure_is_actionable_and_does_not_reveal_password()
    {
        const string password = "must-not-appear-in-errors";
        using var destination = FileStorages.Open(SmbOptions() with { Password = password });
        var error = await Assert.ThrowsAsync<IOException>(() => destination.TestAsync(CancellationToken.None));
        Assert.Contains("authenticate", error.Message);
        Assert.DoesNotContain(password, error.ToString());
    }

    [Fact]
    public void Destination_options_do_not_print_passwords()
    {
        Assert.DoesNotContain("secret", new FileStorageOptions("smb://host/share", "user", "domain", "secret").ToString());
    }

    private IFileStorage OpenLocal() => FileStorages.Open(new(directory, "", "", null));
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    public async Task Bounded_local_read_accepts_exact_limit_and_rejects_one_byte_over(int limit)
    {
        using var destination = OpenLocal();
        using var exact = new MemoryStream(new byte[limit]);
        await destination.PublishNewAsync("exact.kdbx", exact, CancellationToken.None);
        await using (var stream = await destination.OpenReadAsync("exact.kdbx", CancellationToken.None, limit))
            Assert.Equal(limit, stream.Length);
        using var oversized = new MemoryStream(new byte[limit + 1]);
        await destination.PublishNewAsync("oversized.kdbx", oversized, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => destination.OpenReadAsync("oversized.kdbx", CancellationToken.None, limit));
        // A rejected open must release its handle and preserve the source.
        Assert.Equal(limit + 1, new FileInfo(Path.Combine(directory, "oversized.kdbx")).Length);
        await destination.DeleteAsync("oversized.kdbx", CancellationToken.None);
        Assert.False(await destination.ExistsAsync("oversized.kdbx", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Negative_read_limit_fails_before_storage_access(bool smb)
    {
        using var destination = smb ? FileStorages.Open(new("smb://invalid.invalid/share", "", "", null)) : OpenLocal();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => destination.OpenReadAsync("vault.kdbx", CancellationToken.None, -1));
    }

    [SmbFact]
    public async Task Bounded_smb_read_rejects_overflow_without_changing_remote_file()
    {
        using var destination = FileStorages.Open(SmbOptions());
        var name = $"atlas-bounded-{Guid.NewGuid():N}.kdbx";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(65537);
        using var content = new MemoryStream(bytes);
        await destination.PublishNewAsync(name, content, CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => destination.OpenReadAsync(name, CancellationToken.None, 65536));
            await using var read = await destination.OpenReadAsync(name, CancellationToken.None, bytes.Length);
            using var actual = new MemoryStream();
            await read.CopyToAsync(actual);
            Assert.Equal(bytes, actual.ToArray());
        }
        finally
        {
            await destination.DeleteAsync(name, CancellationToken.None);
        }
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));
    private static FileStorageOptions SmbOptions() => new(
        Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_DESTINATION")!,
        Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_USERNAME") ?? "",
        Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_DOMAIN") ?? "",
        Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_PASSWORD"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class InterruptedStream : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new IOException("Source interrupted.");
            read = true;
            "partial bytes"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult(13);
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync("partial bytes"u8.ToArray(), cancellationToken);
            throw new IOException("Source interrupted.");
        }
    }

    public sealed class SmbFactAttribute : FactAttribute
    {
        public SmbFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_TEST_SMB_DESTINATION")))
                Skip = "Set ATLAS_TEST_SMB_DESTINATION and optional USERNAME/DOMAIN/PASSWORD variables for a live direct SMB test.";
        }
    }
}
