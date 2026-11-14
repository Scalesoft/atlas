using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Features.Backups;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Atlas.Server.Tests.StorageProfiles;

public sealed class StorageProfileServiceTests
{
    [PostgreSqlFact]
    public async Task Stored_smb_password_requires_reentry_when_server_or_account_changes()
    {
        await using var host = await AtlasTestHost.CreateAsync("storage_credentials_");
        var service = Service(host);
        var token = Guid.NewGuid().ToString("N")[..8];
        var server = "files" + token;
        var request = Request() with { Destination = $"smb://{server}/wiki/backups", Username = "backup", Domain = "company", Password = "original-password" };
        var saved = await service.SaveAsync(null, request, CancellationToken.None);
        var edit = request with { Password = null, ExpectedVersion = saved.Version };

        foreach (var changed in new[] { edit with { Destination = $"smb://other{token}/wiki/backups" }, edit with { Username = "other" }, edit with { Domain = "other" } })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(saved.Id, changed, CancellationToken.None))).Status);

        var unchanged = await service.GetAsync(saved.Id, CancellationToken.None);
        Assert.Equal((saved.Destination, saved.Username, saved.Domain, saved.Version), (unchanged.Destination, unchanged.Username, unchanged.Domain, unchanged.Version));
        var moved = await service.SaveAsync(saved.Id, edit with { Destination = $"smb://{server.ToUpperInvariant()}/wiki/exports" }, CancellationToken.None);
        Assert.True(moved.HasPassword);
        var replaced = await service.SaveAsync(saved.Id, edit with { Destination = $"smb://other{token}/wiki/exports", Password = "new-password", ExpectedVersion = moved.Version }, CancellationToken.None);
        Assert.Equal($"smb://other{token}/wiki/exports", replaced.Destination);
        Assert.True(replaced.HasPassword);
    }

    [PostgreSqlFact]
    public async Task Restricted_profiles_follow_current_space_and_usage_permissions()
    {
        await using var host = await AtlasTestHost.CreateAsync("storage_profiles_");
        var space = await host.SeedSpaceAsync("Allowed");
        var other = await host.SeedSpaceAsync("Other");
        var service = Service(host);
        var saved = await service.SaveAsync(null, Request() with { SpaceIds = [space] }, CancellationToken.None);
        // Other tests' all-spaces profiles are available everywhere: only this test's profile is looked at.
        Assert.Equal(saved.Id, Assert.Single(await service.ListAvailableAsync(space, CancellationToken.None), x => x.Id == saved.Id).Id);
        Assert.DoesNotContain(await service.ListAvailableAsync(other, CancellationToken.None), x => x.Id == saved.Id);
        using var session = host.OpenSession();
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.RequireAvailableAsync(session, saved.Id, other, CancellationToken.None))).Status);
        await service.SaveAsync(saved.Id, Request() with { AllSpaces = true, AllowPageExports = false, ExpectedVersion = saved.Version }, CancellationToken.None);
        Assert.DoesNotContain(await service.ListAvailableAsync(space, CancellationToken.None), x => x.Id == saved.Id);
        using var storage = await service.OpenAsync(saved.Id, "zip", true, CancellationToken.None);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.OpenAsync(saved.Id, "pdf", false, CancellationToken.None))).Status);
    }

    [PostgreSqlFact]
    public async Task Traversal_and_local_junction_escapes_are_rejected_before_writing()
    {
        await using var host = await AtlasTestHost.CreateAsync("storage_paths_");
        var parent = Path.Combine(Path.GetTempPath(), "atlas-storage-paths", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "root");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var service = Service(host);
            var saved = await service.SaveAsync(null, Request() with { Destination = root }, CancellationToken.None);
            foreach (var folder in new[] { "../outside", "/absolute", "a\\..\\outside", "C:\\outside", "a//b", new string('a', 513), string.Join('/', Enumerable.Repeat("a", 9)) })
                Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.OpenAsync(saved.Id, folder, false, CancellationToken.None))).Status);
            Link(Path.Combine(root, "escape"), outside);
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.OpenAsync(saved.Id, "escape/pdf", false, CancellationToken.None))).Status);
            Assert.Empty(Directory.GetFiles(outside));

            // A junction above the root (as on a developer's workstation) is no escape: the profile stays usable through it.
            Link(Path.Combine(parent, "junction"), outside);
            var through = await service.SaveAsync(null, Request() with { Name = Unique("Through"), Destination = Path.Combine(parent, "junction", "inner") }, CancellationToken.None);
            using var storage = await service.OpenAsync(through.Id, "pdf", false, CancellationToken.None);
            await storage.TestAsync(CancellationToken.None);
        }
        finally
        {
            foreach (var link in new[] { Path.Combine(root, "escape"), Path.Combine(parent, "junction") })
                if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link, false);
            Directory.Delete(parent, true);
        }
    }

    /// <summary>A directory link: a junction on Windows (no privilege needed), a symbolic link elsewhere.</summary>
    internal static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var process = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) process.ArgumentList.Add(argument);
        using var junction = System.Diagnostics.Process.Start(process)!;
        junction.WaitForExit();
        Assert.Equal(0, junction.ExitCode);
    }

    [PostgreSqlFact]
    public async Task Referenced_deletion_and_stale_edits_are_rejected_without_leaking_password()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("storage_references_") /* own database: sets BackupSettings (the one row for the installation, id 1, read by the backup code) to a profile */;
        var service = Service(host);
        var saved = await service.SaveAsync(null, Request() with { Destination = "smb://files/wiki/backups", Password = "private-test-password" }, CancellationToken.None);
        Assert.True(saved.HasPassword);
        Assert.DoesNotContain("private-test-password", System.Text.Json.JsonSerializer.Serialize(saved));
        var changed = await service.SaveAsync(saved.Id, Request() with { Destination = saved.Destination, ExpectedVersion = saved.Version }, CancellationToken.None);
        Assert.True(changed.HasPassword);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.DeleteAsync(saved.Id, saved.Version, CancellationToken.None))).Status);
        await host.SeedAsync(async session => (await session.GetAsync<BackupSettings>(1)).StorageProfileId = saved.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.DeleteAsync(saved.Id, changed.Version, CancellationToken.None))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(null, Request() with { Name = Unique("Other"), Destination = "SMB://FILES/wiki/backups/" }, CancellationToken.None))).Status);
    }

    [PostgreSqlFact]
    public async Task Open_storage_rejects_mutations_after_profile_changes_or_is_disabled()
    {
        await using var host = await AtlasTestHost.CreateAsync("storage_fence_");
        var service = Service(host);
        var request = Request() with { AllSpaces = true };
        var saved = await service.SaveAsync(null, request, CancellationToken.None);
        using var original = await service.OpenAsync(saved.Id, "", false, CancellationToken.None);
        var changed = await service.SaveAsync(saved.Id, request with { Name = Unique("Changed"), Destination = request.Destination + "-new", ExpectedVersion = saved.Version }, CancellationToken.None);
        using var content = new MemoryStream([1, 2, 3]);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => original.PublishNewAsync("document.pdf", content, CancellationToken.None))).Status);
        using var current = await service.OpenAsync(saved.Id, "", false, CancellationToken.None);
        await service.SaveAsync(saved.Id, request with { Enabled = false, ExpectedVersion = changed.Version }, CancellationToken.None);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => current.PublishReplaceAsync("document.pdf", content, CancellationToken.None))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => current.DeleteAsync("document.pdf", CancellationToken.None))).Status);
        Assert.False(Directory.Exists(request.Destination));
        Assert.False(Directory.Exists(changed.Destination));
    }

    [PostgreSqlFact]
    public async Task Nested_local_and_smb_roots_are_rejected_in_both_directions()
    {
        await using var host = await AtlasTestHost.CreateAsync("storage_overlap_");
        var service = Service(host);
        var server = "files" + Guid.NewGuid().ToString("N")[..8];
        var legacy = "legacy" + Guid.NewGuid().ToString("N")[..8];
        var root = Request();
        await service.SaveAsync(null, root, CancellationToken.None);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(null,
            root with { Name = Unique("Nested"), Destination = Path.Combine(root.Destination, "child") }, CancellationToken.None))).Status);
        await service.SaveAsync(null, Request() with { Name = Unique("Remote"), Destination = $"smb://{server}/wiki/pdfs" }, CancellationToken.None);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(null,
            Request() with { Name = Unique("Remote parent"), Destination = $"SMB://{server.ToUpperInvariant()}/wiki/" }, CancellationToken.None))).Status);
        await host.SeedAsync(session => session.SaveAsync(new StorageProfile
        {
            Name = Unique("Legacy"), Destination = $"smb://{legacy}:445/wiki/a b"
        }));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(null,
            Request() with { Name = Unique("Encoded alias"), Destination = $"smb://{legacy}/wiki/a%20b" }, CancellationToken.None))).Status);
    }
    [PostgreSqlFact]
    public async Task Publication_rechecks_profile_after_upload_and_preserves_the_prior_file()
    {
        await using var host = await AtlasTestHost.CreateAsync("storage_commit_");
        var service = Service(host);
        var request = Request();
        var saved = await service.SaveAsync(null, request, CancellationToken.None);
        try
        {
            using var storage = await service.OpenAsync(saved.Id, "", true, CancellationToken.None);
            using var old = new MemoryStream([1, 2, 3]);
            await storage.PublishNewAsync("backup.zip", old, CancellationToken.None);
            using var incoming = new ProfileChangingStream(async () =>
                await service.SaveAsync(saved.Id, request with { Destination = request.Destination + "-new", ExpectedVersion = saved.Version }, CancellationToken.None));
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => storage.PublishReplaceAsync("backup.zip", incoming, CancellationToken.None))).Status);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(request.Destination, "backup.zip")));
            Assert.Single(Directory.GetFiles(request.Destination));
            Assert.False(Directory.Exists(request.Destination + "-new"));
        }
        finally { if (Directory.Exists(request.Destination)) Directory.Delete(request.Destination, true); }
    }

    private sealed class ProfileChangingStream(Func<Task> changed) : MemoryStream([4, 5, 6])
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            await changed();
        }
    }
    private static StorageProfileService Service(AtlasTestHost host) => new(host.Database, new EphemeralDataProtectionProvider(), new AttachmentTests.EnvironmentStub(Path.GetTempPath()));
    private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];
    private static StorageProfileRequest Request() => new(Unique("Shared"), Path.Combine(Path.GetTempPath(), "atlas-storage", Guid.NewGuid().ToString("N")), "", "", null, true, true, true, false, []);
}
