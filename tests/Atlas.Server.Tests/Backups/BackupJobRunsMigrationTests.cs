using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Backups;

/// <summary>Migration 2026110460 over the runs an installation already has: each becomes a run of the ZIP job, a check cut off by the
/// deployment fails and makes the next check due at once, and the archives stay downloadable rows of BackupRun.</summary>
public sealed class BackupJobRunsMigrationTests
{
    [PostgreSqlFact]
    public async Task Finished_failed_and_interrupted_checks_become_runs_of_the_zip_job_and_the_archives_stay()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_job_runs_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110460);
        await ExecuteAsync(schema.ConnectionString, """
            UPDATE "BackupSettings" SET "Enabled" = true, "Mode" = 'full', "Destination" = '/srv/backups', "NextRunAt" = '2026-12-01Z' WHERE "Id" = 1;
            UPDATE "GitBackupSettings" SET "Enabled" = true, "Failures" = 3, "NextAttemptAt" = now(), "LastError" = 'Push byl odmítnut.' WHERE "Id" = 1;
            INSERT INTO "BackupRun"("Id","SettingsId","StartedAt","FinishedAt","Status","Trigger","Mode","Destination","ContentToken","FileName","Size","Message") VALUES
              ('00000000-0000-0000-0000-000000000001', 1, '2026-10-01Z', '2026-10-01 00:01Z', 'succeeded', 'scheduled', 'full', '/srv/backups', 't1', 'wiki-1.zip', 10, 'Záloha byla úspěšně vytvořena.'),
              ('00000000-0000-0000-0000-000000000002', 1, '2026-10-02Z', '2026-10-02 00:01Z', 'skipped', 'scheduled', 'full', '/srv/backups', 't1', NULL, 0, 'Obsah se nezměnil.'),
              ('00000000-0000-0000-0000-000000000003', 1, '2026-10-03Z', '2026-10-03 00:01Z', 'failed', 'manual', 'full', '/srv/backups', NULL, NULL, 0, 'Záloha selhala. Disk je plný.'),
              ('00000000-0000-0000-0000-000000000004', 1, '2026-10-04Z', NULL, 'running', 'scheduled', 'full', '/srv/backups', NULL, NULL, 0, 'Zálohování probíhá.');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(
            "schedule|succeeded|2026-10-01 00:01||Záloha byla úspěšně vytvořena.;"
            + "schedule|succeeded|2026-10-02 00:01||Obsah se nezměnil.;"
            + "manual|failed|2026-10-03 00:01|Záloha selhala. Disk je plný.|Záloha selhala. Disk je plný.;"
            + "schedule|failed|later|Předchozí běh byl přerušen restartem nebo ztrátou připojení. Předchozí zálohy zůstaly zachovány.|"
            + "Předchozí běh byl přerušen restartem nebo ztrátou připojení. Předchozí zálohy zůstaly zachovány.",
            await ScalarAsync(schema.ConnectionString, """
                SELECT string_agg(concat_ws('|', "Trigger", "State",
                  CASE WHEN "FinishedAt" > '2026-10-05Z' THEN 'later' ELSE to_char("FinishedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') END,
                  coalesce("Error", ''), coalesce("Log"->0->>'message', '')), ';' ORDER BY "Id")
                FROM "JobRun" WHERE "JobId" = 'backups.zip'
                """));
        // The interrupted check is failed in BackupRun too, and the next one is due now, not in December.
        Assert.Equal("succeeded,skipped,failed,failed", await ScalarAsync(schema.ConnectionString,
            """SELECT string_agg("Status", ',' ORDER BY "Id") FROM "BackupRun" """));
        Assert.Equal(true, await ScalarAsync(schema.ConnectionString, """SELECT "NextRunAt" < '2026-12-01Z' FROM "BackupSettings" """));
        // The archive keeps its file for the download; the Git backup keeps its last error, its retry counter is gone.
        Assert.Equal("wiki-1.zip", await ScalarAsync(schema.ConnectionString, """SELECT "FileName" FROM "BackupRun" WHERE "Status" = 'succeeded'"""));
        Assert.Equal("Push byl odmítnut.|0", await ScalarAsync(schema.ConnectionString, """
            SELECT "LastError" || '|' || (SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
              AND table_name = 'GitBackupSettings' AND column_name IN ('Failures', 'NextAttemptAt')) FROM "GitBackupSettings"
            """));
    }

    [PostgreSqlFact]
    public async Task An_installation_without_runs_gets_no_job_run_and_keeps_its_schedule()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_job_runs_empty_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110460);
        await ExecuteAsync(schema.ConnectionString, """UPDATE "BackupSettings" SET "Enabled" = true, "Destination" = '/srv/backups', "NextRunAt" = '2026-12-01Z'""");

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'backups.zip'"""));
        Assert.Equal(true, await ScalarAsync(schema.ConnectionString, """SELECT "NextRunAt" = '2026-12-01Z' FROM "BackupSettings" """));
    }
}
