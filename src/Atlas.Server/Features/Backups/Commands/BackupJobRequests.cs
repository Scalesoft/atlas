using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Backups.Commands;

/// <summary>„Zálohovat teď“: a run of the ZIP job now, for a wiki administrator; it merges into one already queued. Returns its id.</summary>
public sealed record RunWikiBackupNow;

/// <summary>After the ZIP settings were saved (the endpoint checked the administrator): the job plans its next run from them.</summary>
public sealed record RescheduleWikiBackup;

/// <summary>After the Git backup was switched on (the endpoint checked the administrator): its first run at once.</summary>
public sealed record RequestGitBackup;

public static class BackupJobRequestsHandler
{
    public static async Task<long> Handle(RunWikiBackupNow command, ModuleSettingsAccess access, IJobControl jobs, CancellationToken ct)
        => await jobs.RunNowAsync(ZipBackupJob.JobId, (await access.RequireWikiAdminAsync(ct)).Id, "Zálohovat teď", ct);

    public static Task Handle(RescheduleWikiBackup command, IJobRequests jobs, CancellationToken ct) => jobs.RescheduleAsync(ZipBackupJob.JobId, ct);

    public static Task Handle(RequestGitBackup command, IJobRequests jobs, CancellationToken ct)
        => jobs.RequestAsync(GitBackupJob.JobId, "Zapnutí zálohy do Gitu", ct: ct);
}
