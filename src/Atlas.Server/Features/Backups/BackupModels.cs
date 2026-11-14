using System.Text.Json.Nodes;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Backups;

public class BackupSettings
{
    public virtual int Id { get; set; } = 1;
    public virtual bool Enabled { get; set; }
    public virtual int IntervalMinutes { get; set; } = 1440;
    public virtual string Mode { get; set; } = "complete";
    public virtual string Destination { get; set; } = "";
    public virtual string Username { get; set; } = "";
    public virtual string Domain { get; set; } = "";
    public virtual string? ProtectedPassword { get; set; }
    public virtual Guid? StorageProfileId { get; set; }
    public virtual string Folder { get; set; } = "";
    protected internal virtual int? StorageProfileVersion { get; set; }
    public virtual int RetentionCount { get; set; } = 10;
    public virtual DateTime? NextRunAt { get; set; }
    /// <summary>The backup key (format v7), protected by the host's Data Protection; see <see cref="Installation.BackupKeys"/>.</summary>
    public virtual string? ProtectedBackupKey { get; set; }
    public virtual string? BackupKeyId { get; set; }
    public virtual DateTime? BackupKeyCreatedAt { get; set; }
}

/// <summary>An archive the ZIP backup wrote (download, retention, the unchanged-content check). Since 3.3 the run state of a check is
/// the job's <see cref="JobRun"/>; rows from before keep their running, skipped and failed states, read-only.</summary>
public class BackupRun
{
    public virtual Guid Id { get; set; } = Guid.NewGuid();
    public virtual int SettingsId { get; set; } = 1;
    public virtual DateTime StartedAt { get; set; }
    public virtual DateTime? FinishedAt { get; set; }
    public virtual string Status { get; set; } = "running";
    public virtual string Trigger { get; set; } = "scheduled";
    public virtual string Mode { get; set; } = "full";
    public virtual string Destination { get; set; } = "";
    public virtual string? ContentToken { get; set; }
    public virtual string? FileName { get; set; }
    public virtual long Size { get; set; }
    public virtual string Message { get; set; } = "";
}

public sealed record BackupSettingsRequest(bool Enabled, int IntervalMinutes, string Mode, string Destination,
    string Username, string Domain, int RetentionCount, string? Password = null, Guid? StorageProfileId = null, string Folder = "");
public sealed record BackupSettingsView(bool Enabled, int IntervalMinutes, string Mode, string Destination,
    string Username, string Domain, int RetentionCount, bool HasPassword, Guid? StorageProfileId = null, string Folder = "");
/// <param name="Runs">The newest 100 archives.</param>
/// <param name="NewBackupKey">The backup key created by enabling the complete mode: present in that one response only.</param>
public sealed record BackupStatus(BackupSettingsView Settings, IReadOnlyList<BackupRun> Runs,
    DateTime? NextRunAt, DateTime? LastSuccessAt, string? Warning, BackupRun? LastBackup, BackupKeyCreated? NewBackupKey = null);

/// <summary>A job's run as its feature page shows it: the state, when (finished, else started, else queued) and its message (the
/// last log line, which for a failure is its message, else the error's first line).</summary>
public sealed record BackupJobRun(long Id, string JobId, string State, DateTime At, string? Message)
{
    // The runner stores the whole exception (type, message, stack) as Error and logs its message: the log reads for a person.
    public static BackupJobRun? Of(JobRun? run) => run is null ? null
        : new(run.Id, run.JobId, run.State, run.FinishedAt ?? run.StartedAt ?? run.QueuedAt,
            (run.Log is { } log && JsonNode.Parse(log) is JsonArray { Count: > 0 } lines ? (string?)lines[^1]?["message"] : null) ?? run.Error?.Split('\n')[0]);
}

/// <summary>The backup key as created: <paramref name="Key"/> is shown to the administrator this once.</summary>
public sealed record BackupKeyCreated(string Key, string KeyId, DateTime CreatedAt);
public sealed record BackupKeyStatus(string? KeyId, DateTime? CreatedAt);
/// <param name="Key">The backup key; without it only the content is restored.</param>
public sealed record InstallationRestoreRequest(string? Key, bool Acknowledge);
