using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Server.Features.Backups;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task Settings_reject_invalid_ranges_and_normalized_public_directory()
    {
        using var fixture = Fixture.WithoutDatabase();
        var invalid = new[]
        {
            fixture.Request with { IntervalMinutes = 4 },
            fixture.Request with { RetentionCount = 0 },
            fixture.Request with { Mode = "unknown" },
            fixture.Request with { Destination = "" },
            fixture.Request with { Destination = Path.Combine(fixture.Root, "wwwroot") + " " },
            fixture.Request with { Destination = Path.Combine(fixture.Root, "wwwroot", "backups") }
        };
        foreach (var request in invalid)
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveSettingsAsync(request, CancellationToken.None))).Status);
    }

    /// <summary>Links are judged by where they lead: a folder under a junction (as on a developer's workstation) is a fine target, a
    /// link into the public wwwroot is not.</summary>
    [Fact]
    public async Task Destination_links_are_judged_by_their_real_target()
    {
        using var fixture = Fixture.WithoutDatabase();
        var real = Directory.CreateDirectory(Path.Combine(fixture.Root, "real")).FullName;
        var web = Directory.CreateDirectory(Path.Combine(fixture.Root, "wwwroot")).FullName;
        StorageProfiles.StorageProfileServiceTests.Link(Path.Combine(fixture.Root, "junction"), real);
        StorageProfiles.StorageProfileServiceTests.Link(Path.Combine(fixture.Root, "public"), web);
        try
        {
            // The save's own check (BackupService.Validate), which runs before anything is read or written.
            fixture.Service.Validate(fixture.Request with { Destination = Path.Combine(fixture.Root, "junction", "backups") });
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveSettingsAsync(
                fixture.Request with { Destination = Path.Combine(fixture.Root, "public", "backups") }, CancellationToken.None))).Status);
        }
        finally
        {
            Directory.Delete(Path.Combine(fixture.Root, "junction"), false);
            Directory.Delete(Path.Combine(fixture.Root, "public"), false);
        }
    }

    [PostgreSqlFact]
    public async Task Download_returns_archive_bytes_and_explains_retention_or_missing_run()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveSettingsAsync(fixture.Request with { RetentionCount = 1 }, CancellationToken.None);
        var first = (await fixture.Service.RunAsync("manual", true, CancellationToken.None))!;
        var downloaded = await fixture.Service.DownloadAsync(first.Id, CancellationToken.None);
        await using (var stream = downloaded.Stream)
        {
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            Assert.Equal(first.FileName, downloaded.Name);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.Target, first.FileName!)), bytes.ToArray());
        }
        await fixture.Service.RunAsync("manual", true, CancellationToken.None);
        Assert.Equal(410, (await Assert.ThrowsAsync<ApiException>(() => fixture.Service.DownloadAsync(first.Id, CancellationToken.None))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => fixture.Service.DownloadAsync(Guid.NewGuid(), CancellationToken.None))).Status);
    }

    [PostgreSqlFact]
    public async Task Externally_missing_archive_is_recreated_even_without_content_changes()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveSettingsAsync(fixture.Request, CancellationToken.None);
        var first = (await fixture.Service.RunAsync("manual", true, CancellationToken.None))!;
        File.Delete(Path.Combine(fixture.Target, first.FileName!));
        var second = (await fixture.Service.RunAsync("manual", false, CancellationToken.None))!;
        Assert.Equal("succeeded", second.Status);
        Assert.NotEqual(first.FileName, second.FileName);
        Assert.True(File.Exists(Path.Combine(fixture.Target, second.FileName!)));
    }

    [PostgreSqlFact]
    public async Task Last_successful_archive_remains_available_beyond_recent_history()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveSettingsAsync(fixture.Request, CancellationToken.None);
        var first = await fixture.Service.RunAsync("manual", true, CancellationToken.None);
        await fixture.Host.SeedAsync(async session =>
        {
            for (var i = 1; i <= 101; i++)
                await session.SaveAsync(new BackupRun { StartedAt = first!.StartedAt.AddMinutes(i), Status = "succeeded", Destination = fixture.Target });
        });
        var status = await fixture.Service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(100, status.Runs.Count);
        Assert.DoesNotContain(status.Runs, x => x.Id == first!.Id);
        Assert.Equal(first!.Id, status.LastBackup!.Id);
        Assert.NotNull(status.LastBackup.FileName);
    }

    [PostgreSqlFact]
    public async Task Settings_roundtrip_hides_password_and_preserves_it_on_blank_save()
    {
        await using var fixture = await Fixture.Create();
        var request = fixture.Request with { Destination = "smb://files/wiki/backups", Username = "backup", Password = "private-test-password" };
        var saved = await fixture.Service.SaveSettingsAsync(request, CancellationToken.None);
        Assert.True(saved.Settings.HasPassword);
        Assert.DoesNotContain("private-test-password", System.Text.Json.JsonSerializer.Serialize(saved));
        var second = await fixture.Service.SaveSettingsAsync(request with { Password = "" }, CancellationToken.None);
        Assert.True(second.Settings.HasPassword);
        Assert.NotNull(second.NextRunAt);
    }

    [PostgreSqlFact]
    public async Task Unchanged_run_reports_skip_and_preserves_existing_archive()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveSettingsAsync(fixture.Request, CancellationToken.None);
        var first = await fixture.Service.RunAsync("manual", false, CancellationToken.None);
        Assert.Equal("succeeded", first!.Status);
        var path = Path.Combine(fixture.Target, first.FileName!);
        var bytes = await File.ReadAllBytesAsync(path);
        var second = await fixture.Service.RunAsync("manual", false, CancellationToken.None);
        Assert.Equal("skipped", second!.Status);
        Assert.Equal(BackupPolicy.NoChanges, second.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(fixture.Target, "*.zip"));
    }

    [PostgreSqlFact]
    public async Task Failed_target_does_not_replace_success_checkpoint_and_test_exercises_permissions()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveSettingsAsync(fixture.Request, CancellationToken.None);
        await fixture.Service.TestDestinationAsync(fixture.Request, CancellationToken.None);
        Assert.Empty(Directory.GetFiles(fixture.Target));
        var first = await fixture.Service.RunAsync("manual", true, CancellationToken.None);
        Assert.Equal("succeeded", first!.Status);
        var blocked = Path.Combine(fixture.Root, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "file");
        await fixture.Service.SaveSettingsAsync(fixture.Request with { Destination = blocked }, CancellationToken.None);
        var failed = await fixture.Service.RunAsync("manual", false, CancellationToken.None);
        Assert.Equal("failed", failed!.Status);
        // A failed check stores nothing: the last archive stays the checkpoint (its warning is the job run's, see the job test).
        Assert.Equal(first.Id, (await fixture.Service.GetStatusAsync(CancellationToken.None)).LastBackup!.Id);
        Assert.True(File.Exists(Path.Combine(fixture.Target, first.FileName!)));
    }

    [PostgreSqlFact]
    public async Task Only_due_enabled_scheduled_run_executes_and_retention_keeps_latest()
    {
        await using var fixture = await Fixture.Create();
        Assert.Null(await fixture.Service.RunAsync("scheduled", false, CancellationToken.None));
        Assert.Null(await fixture.Service.NextDueAsync(CancellationToken.None));
        var saved = await fixture.Service.SaveSettingsAsync(fixture.Request with { RetentionCount = 1 }, CancellationToken.None);
        // The job's data schedule: a save is due at once, every check plans the next one an interval later.
        Assert.Equal(saved.NextRunAt, await fixture.Service.NextDueAsync(CancellationToken.None));
        var first = await fixture.Service.RunAsync("manual", true, CancellationToken.None);
        Assert.InRange((await fixture.Service.NextDueAsync(CancellationToken.None))!.Value - first!.StartedAt, TimeSpan.FromMinutes(1439), TimeSpan.FromMinutes(1441));
        Assert.Null(await fixture.Service.RunAsync("scheduled", false, CancellationToken.None));
        var second = await fixture.Service.RunAsync("manual", true, CancellationToken.None);
        Assert.Equal("succeeded", second!.Status);
        Assert.False(File.Exists(Path.Combine(fixture.Target, first!.FileName!)));
        Assert.True(File.Exists(Path.Combine(fixture.Target, second.FileName!)));
        await fixture.Service.SaveSettingsAsync(fixture.Request with { Enabled = false }, CancellationToken.None);
        Assert.Null(await fixture.Service.NextDueAsync(CancellationToken.None));
    }

    /// <summary>The ZIP job (plan row 10): its run's log says what the check did, only an archive becomes a BackupRun, and a failed check
    /// fails the run with its reason, which the page shows as the last run and its warning.</summary>
    [PostgreSqlFact]
    public async Task The_zip_job_logs_each_check_stores_only_archives_and_fails_with_the_reason()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveSettingsAsync(fixture.Request, CancellationToken.None);

        var created = await fixture.RunJobAsync();
        var unchanged = await fixture.RunJobAsync();
        Assert.Equal(("succeeded", "Záloha byla úspěšně vytvořena."), (created.State, BackupJobRun.Of(created)!.Message));
        Assert.Equal(("succeeded", BackupPolicy.NoChanges), (unchanged.State, BackupJobRun.Of(unchanged)!.Message));
        var blocked = Path.Combine(fixture.Root, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "file");
        await fixture.Service.SaveSettingsAsync(fixture.Request with { Destination = blocked }, CancellationToken.None);
        var failed = await fixture.RunJobAsync();

        Assert.Equal("failed", failed.State);
        Assert.StartsWith("Záloha selhala.", BackupJobRun.Of(failed)!.Message);
        var status = await fixture.Service.GetStatusAsync(CancellationToken.None);
        Assert.Equal("succeeded", Assert.Single(status.Runs).Status);
        var last = await fixture.Service.LastRunAsync(CancellationToken.None);
        Assert.Equal((failed.Id, "failed"), (last!.Id, last.State));
        Assert.StartsWith("Poslední zálohování selhalo", status.Warning);
        Assert.Single(Directory.GetFiles(fixture.Target, "*.zip"));
    }

    [PostgreSqlFact]
    public async Task Profile_backup_uses_relative_folder_and_stops_when_profile_is_disabled()
    {
        await using var fixture = await Fixture.Create();
        var profile = new Atlas.Server.Features.StorageProfiles.StorageProfile
        {
            Name = "Backup", Destination = fixture.Target, AllowPageExports = false
        };
        await fixture.Host.SeedAsync(session => session.SaveAsync(profile));
        var request = fixture.Request with { Destination = "", StorageProfileId = profile.Id, Folder = "daily/zip" };
        await fixture.Service.SaveSettingsAsync(request, CancellationToken.None);
        var run = (await fixture.Service.RunAsync("manual", true, CancellationToken.None))!;
        Assert.Equal("succeeded", run.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Target, "daily", "zip", run.FileName!)));
        await fixture.Host.SeedAsync(async session => (await session.GetAsync<Atlas.Server.Features.StorageProfiles.StorageProfile>(profile.Id)).Enabled = false);
        var blocked = (await fixture.Service.RunAsync("manual", true, CancellationToken.None))!;
        Assert.Equal("failed", blocked.Status);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Target, "daily", "zip")));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required AtlasTestHost Host { get; init; }
        public required string Root { get; init; }
        public required BackupService Service { get; init; }
        public string Target => Path.Combine(Root, "backups");
        public BackupSettingsRequest Request => new(true, 1440, "full", Target, "", "", 10);
        public static async Task<Fixture> Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "atlas-backup-service-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var environment = new AttachmentTests.EnvironmentStub(root);
            var attachments = new AttachmentFiles(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), environment);
            // Own host: the settings are the one BackupSettings row and the runs the BackupRun table of the installation, and an archive
            // holds the whole wiki.
            var host = await AtlasTestHost.CreateAsync("backup_service_", services =>
            {
                services.AddSingleton(provider => new BackupService(provider.GetRequiredService<AtlasDatabase>(),
                    new WikiBackupArchive(provider.GetRequiredService<AtlasDatabase>(), attachments), new EphemeralDataProtectionProvider(),
                    environment, NullLogger<BackupService>.Instance));
                services.AddJob<ZipBackupJob>();
            });
            return new Fixture { Host = host, Root = root, Service = host.Services.GetRequiredService<BackupService>() };
        }

        public static Unconnected WithoutDatabase()
        {
            var root = Path.Combine(Path.GetTempPath(), "atlas-backup-service-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var environment = new AttachmentTests.EnvironmentStub(root);
            return new Unconnected
            {
                Root = root,
                Service = new BackupService(null!, new WikiBackupArchive(null!, null!), new EphemeralDataProtectionProvider(), environment, NullLogger<BackupService>.Instance)
            };
        }

        /// <summary>One run of the ZIP job to its end, as „Zálohovat teď“ starts it.</summary>
        public async Task<JobRun> RunJobAsync()
        {
            var id = await Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(ZipBackupJob.JobId, "test");
            using var session = Host.OpenSession();
            return await session.GetAsync<JobRun>(id);
        }
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }

    /// <summary>The service over no database, for its checks of a request, which run before anything is read or written.</summary>
    private sealed class Unconnected : IDisposable
    {
        public required string Root { get; init; }
        public required BackupService Service { get; init; }
        public BackupSettingsRequest Request => new(true, 1440, "full", Path.Combine(Root, "backups"), "", "", 10);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
