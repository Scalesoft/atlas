using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Backups;

/// <summary>
/// The ZIP backup (3.3 plan, row 10) at the time its settings plan: <see cref="BackupSettings.NextRunAt"/>, moved by the interval at
/// every check and set to now by a save of the settings (which reschedules). „Zálohovat teď“ is the job's run now. A check stores only
/// a new archive (<see cref="BackupRun"/>); its outcome is the run's log, a failure its error.
/// </summary>
public sealed class ZipBackupJob : IJobDefinition
{
    public const string JobId = "backups.zip";

    public string Id => JobId;
    public string Title => "Záloha ZIP";
    public string Description => "V nastavený čas zapíše archiv ZIP do cíle záloh, když se obsah od poslední zálohy změnil, a starší archivy odstraní podle retence. Začne až po minutě, kdy nikdo needitoval.";

    // The next planned time is the retry, as before the jobs.
    public JobRetry Retry => JobRetry.None;
    public JobSchedule? Schedule => JobSchedule.FromData;
    public bool RunsWhenQuiet => true;

    public Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<BackupService>().NextDueAsync(ct);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var run = await context.Services.GetRequiredService<BackupService>().RunAsync(context.Trigger == JobTriggers.Schedule ? "scheduled" : "manual", false, ct);
        // Stopped or shut down: the runner decides, the check stored nothing.
        ct.ThrowIfCancellationRequested();
        if (run?.Status == "failed")
            throw new JobFailedException(run.Message);
        context.Log.LogInformation("{Message}", run?.Message ?? "Plánovaná záloha ještě není na řadě.");
    }
}

/// <summary>
/// The Git backup (3.3 plan, row 11; owner decision O6): a page's publication asks for a run (<see cref="GitBackupRevision"/>), and
/// <see cref="GitBackupService.FullComparisonAfter"/> after the previous run a run compares the whole wiki and snapshots the other modules
/// when that is due (<see cref="GitBackupService.NextDueAsync"/>). Every automatic run starts only once no page was edited for a minute
/// (<see cref="JobQuiet"/>), so the time counts from real quiet. A failed run retries by the default JobRetry; „Synchronizovat teď“
/// runs in its request, outside the job, and the run lock keeps the two apart.
/// </summary>
public sealed class GitBackupJob : IJobDefinition
{
    public const string JobId = "backups.git";

    public string Id => JobId;
    public string Title => "Záloha do Gitu";
    public string Description => "Po publikaci stránky zapíše změny wiki jako commity do repozitáře zálohy; 10 minut po předchozím běhu porovná celou wiki a zapíše snímek ostatních modulů. Začne vždy až po minutě, kdy nikdo needitoval.";

    public JobSchedule? Schedule => JobSchedule.FromData;
    public bool RunsWhenQuiet => true;

    public Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<GitBackupService>().NextDueAsync(ct);

    public Task RunAsync(JobContext context, CancellationToken ct) => context.Services.GetRequiredService<GitBackupService>().SyncAsync(false, ct);
}
