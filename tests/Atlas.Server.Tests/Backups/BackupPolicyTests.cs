using Atlas.Server.Features.Backups;
using Xunit;
namespace Atlas.Server.Tests.Backups;

public sealed class BackupPolicyTests
{
    private static BackupJobRun Run(string state, DateTime at, string? message = null) => new(1, ZipBackupJob.JobId, state, at, message);

    [Fact]
    public void Unconfigured_and_disabled_backups_warn_but_skipped_run_is_healthy()
    {
        var now = new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc);
        var settings = new BackupSettings();
        Assert.NotNull(BackupPolicy.Warning(settings, null, now));
        settings.Enabled = true;
        settings.Destination = Path.GetTempPath();
        settings.NextRunAt = now.AddDays(1);
        Assert.Null(BackupPolicy.Warning(settings, Run("succeeded", now, BackupPolicy.NoChanges), now));
        Assert.Null(BackupPolicy.Warning(settings, Run("queued", now), now));
    }

    [Fact]
    public void Failed_long_running_retention_and_overdue_runs_warn_admin()
    {
        var now = new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc);
        var settings = new BackupSettings { Enabled = true, Destination = Path.GetTempPath(), NextRunAt = now.AddDays(1) };
        Assert.StartsWith("Poslední zálohování selhalo", BackupPolicy.Warning(settings, Run("failed", now, "Záloha selhala."), now));
        Assert.Null(BackupPolicy.Warning(settings, Run("running", now.AddHours(-1)), now));
        Assert.Equal("Zálohování běží neobvykle dlouho.", BackupPolicy.Warning(settings, Run("running", now.AddHours(-3)), now));
        Assert.StartsWith("Záloha je vytvořená", BackupPolicy.Warning(settings,
            Run("succeeded", now, "Záloha byla úspěšně vytvořena. Retence: starší archivy se nepodařilo odstranit."), now));
        settings.NextRunAt = now.AddMinutes(-10);
        Assert.NotNull(BackupPolicy.Warning(settings, null, now));
    }

    [Fact]
    public void Retention_preserves_last_good_archive_and_ignores_skips_and_other_targets()
    {
        var runs = new[]
        {
            new BackupRun { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Status = "succeeded", Destination = "/backups", FileName = "old.zip", StartedAt = DateTime.UnixEpoch },
            new BackupRun { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Status = "succeeded", Destination = "/backups", FileName = "new.zip", StartedAt = DateTime.UnixEpoch.AddDays(1) },
            new BackupRun { Status = "skipped", Destination = "/backups", StartedAt = DateTime.UnixEpoch.AddDays(2) },
            new BackupRun { Status = "succeeded", Destination = "/other", FileName = "other.zip" }
        };
        var removed = BackupPolicy.Expired(runs, "/backups", 1);
        Assert.Equal(new[] { Guid.Parse("00000000-0000-0000-0000-000000000001") }, removed.Select(x => x.Id));
    }

    [Fact]
    public void A_zip_backup_not_set_up_or_off_is_no_warning_while_the_git_backup_is_on()
    {
        var now = DateTime.UtcNow;
        Assert.Null(BackupPolicy.Warning(new BackupSettings(), null, now, gitBackupEnabled: true));
        Assert.Null(BackupPolicy.Warning(new BackupSettings { Destination = "/backups", Enabled = false }, null, now, gitBackupEnabled: true));
        Assert.Equal("Zálohování není nastavené.", BackupPolicy.Warning(new BackupSettings(), null, now, gitBackupEnabled: false));
    }

    [Fact]
    public void The_page_reads_a_runs_message_from_its_last_log_line_else_its_error()
    {
        var at = new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc);
        Assert.Null(BackupJobRun.Of(null));
        // As the runner writes a failure: the whole exception as Error, its message as the last log line.
        var failed = BackupJobRun.Of(new Atlas.Server.Platform.Jobs.JobRun
        {
            Id = 7, JobId = ZipBackupJob.JobId, State = "failed", QueuedAt = at, StartedAt = at,
            Error = "Atlas.Server.Platform.Jobs.JobFailedException: Záloha selhala. Disk je plný.\n   at Atlas…",
            Log = """[{"at":"2026-11-01T02:00:00Z","level":"Error","message":"Záloha selhala. Disk je plný."}]"""
        })!;
        Assert.Equal((7L, "failed", at, "Záloha selhala. Disk je plný."), (failed.Id, failed.State, failed.At, failed.Message));
        Assert.Equal("Záloha selhala.", BackupJobRun.Of(new Atlas.Server.Platform.Jobs.JobRun
        {
            JobId = ZipBackupJob.JobId, State = "failed", QueuedAt = at, Error = "Záloha selhala.\n   at Atlas…"
        })!.Message);
        var done = BackupJobRun.Of(new Atlas.Server.Platform.Jobs.JobRun
        {
            JobId = ZipBackupJob.JobId, State = "succeeded", QueuedAt = at, FinishedAt = at.AddMinutes(1),
            Log = """[{"at":"2026-11-01T02:00:00Z","level":"Information","message":"Začátek"},{"at":"2026-11-01T02:01:00Z","level":"Information","message":"Hotovo"}]"""
        })!;
        Assert.Equal((at.AddMinutes(1), "Hotovo"), (done.At, done.Message));
        Assert.Null(BackupJobRun.Of(new Atlas.Server.Platform.Jobs.JobRun { JobId = ZipBackupJob.JobId, QueuedAt = at })!.Message);
    }
}
