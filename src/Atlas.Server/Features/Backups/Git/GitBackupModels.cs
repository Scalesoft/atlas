namespace Atlas.Server.Features.Backups.Git;

public class GitBackupSettings
{
    public virtual int Id { get; set; } = 1;
    public virtual bool Enabled { get; set; }
    public virtual string RemoteUrl { get; set; } = "";
    public virtual string Branch { get; set; } = "main";
    public virtual Guid RepositoryId { get; set; }
    public virtual string? ProtectedPrivateKey { get; set; }
    public virtual string? PublicKey { get; set; }
    public virtual string KnownHosts { get; set; } = "";
    public virtual DateTime? LastSyncAt { get; set; }
    public virtual string? LastCommit { get; set; }
    public virtual string? LastError { get; set; }
    public virtual DateTime? LastErrorAt { get; set; }
}

/// <summary>What the repository holds for one page after the last successful push.</summary>
public class GitBackupPage
{
    public virtual Guid PageId { get; set; }
    /// <summary>Repository path without extension (<c>space/parent/page</c>); NULL once the page left the tree (trash).</summary>
    public virtual string? Path { get; set; }
    public virtual int Version { get; set; }
    public virtual int? PublishedVersion { get; set; }
}

public sealed record GitBackupSettingsRequest(bool Enabled, string RemoteUrl, string Branch);
public sealed record GitBackupTestRequest(string RemoteUrl, string Branch);
public sealed record GitBackupSettingsView(bool Enabled, string RemoteUrl, string Branch, string? PublicKey, IReadOnlyList<string> HostKeys);
public sealed record GitBackupProgress(int Done, int Total);
/// <param name="NewBackupKey">The backup key created by enabling the backup: present in that one response only.</param>
public sealed record GitBackupStatus(GitBackupSettingsView Settings, bool Running, GitBackupProgress? Progress, int PendingPages,
    DateTime? LastSyncAt, string? LastCommit, string? LastError, DateTime? LastErrorAt, string? Warning,
    BackupKeyCreated? NewBackupKey = null);
