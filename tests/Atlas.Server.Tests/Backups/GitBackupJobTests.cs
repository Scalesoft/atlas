using Atlas.Server.Features.Backups;
using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Platform.Events;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Backups;

/// <summary>The Git backup as a job (plan row 11, owner decision O6): a page's publication asks for a run while the backup is on, the
/// requests merge into one queued run, and nothing else asks. This host routes no job message, so a requested run stays queued.</summary>
public sealed class GitBackupJobTests
{
    [PostgreSqlFact]
    public async Task A_publication_asks_for_one_run_while_the_backup_is_on_and_nothing_else_asks()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_git_backup_job_", services => services.AddWikiBackups());
        var page = Guid.NewGuid();
        Task Revised(string contentType, string kind) => host.InvokeAsync(new GitBackupRevision(new ContentRevised(page, contentType, kind, Guid.NewGuid())));

        await Revised("atlas.page", "published");
        Assert.Equal("", await QueuedAsync(host));
        await ExecuteAsync(host.ConnectionString, """UPDATE "GitBackupSettings" SET "Enabled" = true""");
        await Revised("atlas.page", "working");
        await Revised("atlas.contact", "published");
        Assert.Equal("", await QueuedAsync(host));
        await Revised("atlas.page", "published");
        await Revised("atlas.page", "published");

        Assert.Equal("event|Publikace stránky", await QueuedAsync(host));
    }

    /// <summary>The queued runs of the Git backup job as „trigger|reason“, one per line.</summary>
    private static async Task<string> QueuedAsync(AtlasTestHost host)
        => (string)(await ScalarAsync(host.ConnectionString, $"""
            SELECT coalesce(string_agg("Trigger" || '|' || "Reason", E'\n' ORDER BY "Id"), '') FROM "JobRun"
            WHERE "JobId" = '{GitBackupJob.JobId}' AND "State" = 'queued'
            """))!;
}
