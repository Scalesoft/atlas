using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Platform.Jobs;
using Microsoft.AspNetCore.DataProtection;
using NHibernate;

namespace Atlas.Server.Features.Backups.Git;

/// <summary>
/// Continuous git backup (docs/git-backup.md): settings and the SSH deploy key, the access test, and one synchronisation run at a
/// time (PostgreSQL advisory lock: the job's run and the manual one) that prepares the local clone, commits pending changes through
/// <see cref="GitBackupSync"/> and pushes. The local clone is a cache; the database state and the remote are the truth.
/// </summary>
public sealed partial class GitBackupService
{
    /// <summary>Time after a run when the job's run compares the whole wiki, for what no publication announces (moves, space renames, rows
    /// edited by hand). The run itself waits for quiet (<see cref="JobQuiet"/>), so this counts from a quiet moment.</summary>
    public static readonly TimeSpan FullComparisonAfter = TimeSpan.FromMinutes(10);
    /// <summary>
    /// How often the job's run looks for changes outside the pages (accounts, settings, boards, live collaboration state) and
    /// commits them as one snapshot. A run for page revisions snapshots everything anyway; this bounds how much live editing state
    /// enters the history.
    /// </summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(15);

    private readonly AtlasDatabase m_database;
    private readonly GitBackupSync m_sync;
    private readonly IDataProtector m_deployKeys;
    private readonly IConfiguration m_configuration;
    private readonly ILogger<GitBackupService> m_logger;
    private readonly string m_dataPath;
    private readonly InstallationExport m_export;
    private readonly BackupKeys m_keys;
    private readonly TimeProvider m_clock;
    private volatile GitBackupProgress? m_progress;
    private volatile bool m_running;
    private bool m_reconciled;
    // What the last snapshot exported, for the next incremental one; in memory, so a restart compares everything once.
    private long? m_snapshotSince;
    private string? m_snapshotToken;
    private DateTimeOffset? m_snapshotCheckedAt;
    // The write counts (InstallationExport.WatermarkAsync) and the key the last snapshot or token check covered: equal ones need no scan.
    private string? m_snapshotWatermark;

    public GitBackupService(AtlasDatabase database, GitBackupSync sync, IDataProtectionProvider protection, IWebHostEnvironment environment,
        IConfiguration configuration, ILogger<GitBackupService> logger, InstallationExport export, BackupKeys keys, TimeProvider? clock = null)
    {
        m_database = database;
        m_sync = sync;
        m_export = export;
        m_keys = keys;
        m_clock = clock ?? TimeProvider.System;
        m_deployKeys = protection.CreateProtector("Atlas.GitBackup.DeployKey.v1");
        m_configuration = configuration;
        m_logger = logger;
        m_dataPath = Path.GetFullPath(configuration["GitBackup:DataPath"] ?? Path.Combine(environment.ContentRootPath, ".data", "git-backup"));
        var web = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (m_dataPath.Equals(Path.GetPathRoot(m_dataPath), comparison) || m_dataPath.Equals(web, comparison)
            || m_dataPath.StartsWith(web + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("GitBackup:DataPath must be a dedicated directory outside wwwroot.");
    }

    public async Task<GitBackupStatus> GetStatusAsync(CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        var pending = settings.Enabled || settings.RemoteUrl.Length > 0 ? await m_sync.PendingAsync(ct) : 0;
        return Status(settings, pending);
    }

    /// <summary>The warning for the administration badge, or null when the git backup is off or healthy.</summary>
    public async Task<string?> WarningAsync(CancellationToken ct) => Warning(await LoadAsync(ct));

    private static string? Warning(GitBackupSettings settings)
        => !settings.Enabled ? null
            : settings.PublicKey is null || settings.RemoteUrl.Length == 0 ? "Záloha do Gitu je zapnutá, ale nemá nastavený repozitář nebo klíč."
            : settings.LastError is { } error ? "Záloha do Gitu selhala: " + error
            : null;

    /// <summary>The 400 of a request, before its destination is authorized (AuthorizeBackupDestination); the URL as it is stored.</summary>
    public string Validate(GitBackupSettingsRequest request)
    {
        ValidateBranch(request.Branch);
        return ValidateUrl(request.RemoteUrl, required: request.Enabled, AllowFileRemotes);
    }

    /// <param name="approval">The destination changes a fresh sign-in approved, audited with this save (SEC3-4).</param>
    public async Task<GitBackupStatus> SaveSettingsAsync(GitBackupSettingsRequest request, CancellationToken ct, Commands.BackupDestinationApproval? approval = null)
    {
        var url = ValidateUrl(request.RemoteUrl, required: request.Enabled, AllowFileRemotes);
        var branch = ValidateBranch(request.Branch);
        BackupKeyCreated? created = null;
        using (var session = m_database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var settings = await session.GetAsync<GitBackupSettings>(1, LockMode.Upgrade, ct);
            if (request.Enabled && settings.PublicKey is null)
                throw new ApiException(400, "Před zapnutím vygenerujte klíč a vložte ho do repozitáře jako deploy klíč se zápisem.");
            if (settings.RemoteUrl != url || settings.Branch != branch)
            {
                // The next run starts a new local clone. An empty target gets the whole history (PrepareAsync); another form of
                // the same repository continues where it was, because the marker and the committed files match.
                if (!SshFiles.Knows(settings.KnownHosts, Host(url)))
                    settings.KnownHosts = "";
                settings.LastCommit = null;
                settings.LastSyncAt = null;
            }
            settings.Enabled = request.Enabled;
            settings.RemoteUrl = url;
            settings.Branch = branch;
            settings.LastError = null;
            settings.LastErrorAt = null;
            // Enabling creates the backup key the secure part needs; this response is the one time it is shown.
            var backup = await session.GetAsync<BackupSettings>(1, LockMode.Upgrade, ct);
            if (approval is not null)
                await approval.WriteAsync(session, ct);
            if (request.Enabled && backup.ProtectedBackupKey is null)
            {
                var key = m_keys.Replace(backup);
                await Commands.BackupDestinationApproval.WriteKeyAsync(approval, session, key.Id, ct);
                created = new BackupKeyCreated(key.Text, key.Id, backup.BackupKeyCreatedAt!.Value);
            }
            await tx.CommitAsync(ct);
        }
        return await GetStatusAsync(ct) with { NewBackupKey = created };
    }

    /// <summary>A new Ed25519 deploy key; the private half never leaves the server.</summary>
    public async Task<GitBackupStatus> GenerateKeyAsync(CancellationToken ct, Commands.BackupDestinationApproval? approval = null)
    {
        Directory.CreateDirectory(m_dataPath);
        var folder = Path.Combine(m_dataPath, "keygen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "id_ed25519");
            var git = new GitCommand(folder, GitCommand.Environment(m_dataPath, null));
            await git.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", "atlas-git-backup", "-f", file], null, null, true, ct);
            var privateKey = await File.ReadAllTextAsync(file, ct);
            var publicKey = (await File.ReadAllTextAsync(file + ".pub", ct)).Trim();
            using var session = m_database.OpenSession();
            using var tx = session.BeginTransaction();
            var settings = await session.GetAsync<GitBackupSettings>(1, LockMode.Upgrade, ct);
            settings.ProtectedPrivateKey = m_deployKeys.Protect(privateKey);
            settings.PublicKey = publicKey;
            if (approval is not null)
                await approval.WriteAsync(session, ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            DeleteDirectory(folder);
        }
        return await GetStatusAsync(ct);
    }

    /// <summary>Reads the remote and dry-runs a push of a throwaway ref: proves the key can write without changing anything.</summary>
    public async Task<string> TestAsync(GitBackupTestRequest request, CancellationToken ct)
    {
        var url = ValidateUrl(request.RemoteUrl, required: true, AllowFileRemotes);
        var branch = ValidateBranch(request.Branch);
        var settings = await LoadAsync(ct);
        if (settings.ProtectedPrivateKey is null)
            throw new ApiException(400, "Nejprve vygenerujte klíč a vložte ho do repozitáře jako deploy klíč se zápisem.");
        // accept-new appends the tested server; the key of the configured one stays pinned.
        var folder = Path.Combine(m_dataPath, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var ssh = await SshFiles.CreateAsync(m_dataPath, m_deployKeys.Unprotect(settings.ProtectedPrivateKey), settings.KnownHosts, ct);
            var git = new GitCommand(folder, GitCommand.Environment(m_dataPath, ssh.Command));
            var heads = await RemoteHeadsAsync(git, url, ct);
            if (heads.Count > 0 && !heads.Contains(branch))
                throw new ApiException(400, "Repozitář není prázdný a nemá větev {branch} se zálohou Atlasu. Použijte prázdný repozitář.", args: new Dictionary<string, object?> { ["branch"] = branch });
            await git.GitAsync(ct, "init", "--quiet", "--initial-branch=atlas-access-check");
            await git.GitAsync(ct, "commit", "--quiet", "--allow-empty", "--no-verify", "-m", "Atlas access check");
            await git.RunGitAsync(["push", "--dry-run", "--porcelain", url, "HEAD:refs/heads/atlas-access-check-" + Guid.NewGuid().ToString("N")[..8]],
                null, GitCommand.NetworkTimeout, true, ct);
            await SaveKnownHostsAsync(ssh, ct);
            return heads.Count == 0
                ? "Přístup ověřen: čtení i zápis. Repozitář je prázdný, první synchronizace do něj zapíše celou historii."
                : $"Přístup ověřen: čtení i zápis. Větev {branch} už existuje; synchronizace pokračuje jen v záloze této wiki.";
        }
        catch (GitException ex)
        {
            throw new ApiException(400, "Test přístupu selhal: " + ex.Message);
        }
        finally
        {
            DeleteDirectory(folder);
        }
    }

    /// <summary>
    /// One synchronisation. A run of the job (<see cref="GitBackupJob"/>) skips when the backup is off, when the manual run holds the
    /// lock or when nothing changed since a good run, and fails (so the job retries) when the run failed; a manual run waits for a
    /// running one, always reconciles the tree and reports its outcome.
    /// </summary>
    public async Task<GitBackupStatus?> SyncAsync(bool manual, CancellationToken ct)
    {
        if (!manual && !(await LoadAsync(ct)).Enabled)
            return null;
        await using var gate = await BackupRunLock.TryAcquireAsync(m_configuration, BackupRunLock.GitBackup,
            manual ? TimeSpan.FromMinutes(2) : TimeSpan.Zero, ct);
        if (gate is null)
            return manual ? throw new ApiException(409, "Synchronizace do Gitu stále probíhá. Zkuste to znovu za chvíli.") : null;
        RemoveStaleFolders();
        var settings = await LoadAsync(ct);
        if (manual && (settings.PublicKey is null || settings.RemoteUrl.Length == 0))
            throw new ApiException(400, "Nejprve nastavte repozitář, vygenerujte klíč a uložte nastavení.");
        if (!manual && (!settings.Enabled || settings.PublicKey is null || settings.RemoteUrl.Length == 0))
            return null;
        var reconcile = manual || !m_reconciled;
        // After a failure the commits may be in the local clone only: the retry pushes them even with nothing new to compare.
        if (!manual && !reconcile && settings.LastError is null && await m_sync.PendingAsync(ct) == 0 && !await SnapshotDueAsync(ct))
            return null;
        var error = await RunAsync(settings, reconcile, ct);
        if (manual)
            return await GetStatusAsync(ct);
        // LastError tells the page and the warning; the job's JobRetry tries again.
        return error is null ? null : throw new JobFailedException(error, retry: true);
    }

    /// <summary>When the job compares again (<see cref="JobSchedule.FromData"/>, asked after every run): <see cref="FullComparisonAfter"/>
    /// after this run, or when the snapshot is due if that is sooner; never while the backup is off.</summary>
    public async Task<DateTime?> NextDueAsync(CancellationToken ct)
    {
        if (!(await LoadAsync(ct)).Enabled)
            return null;
        var now = m_clock.GetUtcNow();
        var snapshot = (m_snapshotCheckedAt ?? now) + SnapshotInterval;
        return (snapshot < now + FullComparisonAfter ? snapshot : now + FullComparisonAfter).UtcDateTime;
    }

    /// <summary>
    /// Whether a run without page changes should snapshot the other modules: at most every <see cref="SnapshotInterval"/>
    /// it compares the write counts of the backed-up tables with the last snapshot, and only when they moved, the change stamps
    /// of all modules (a scan of every table). Called with the run lock held.
    /// </summary>
    private async Task<bool> SnapshotDueAsync(CancellationToken ct)
    {
        var now = m_clock.GetUtcNow();
        if (m_snapshotCheckedAt is { } checkedAt && now - checkedAt < SnapshotInterval)
            return false;
        m_snapshotCheckedAt = now;
        using var session = m_database.OpenSession();
        session.DefaultReadOnly = true;
        using var tx = session.BeginTransaction();
        var counted = await m_export.WatermarkAsync(session, ct);
        var key = await m_keys.CurrentAsync(session, ct);
        var watermark = Watermark(counted, key);
        if (watermark is not null && watermark == m_snapshotWatermark)
        {
            await tx.CommitAsync(ct);
            return false;
        }
        var token = await m_export.ContentTokenAsync(session, key, ct);
        await tx.CommitAsync(ct);
        if (token != m_snapshotToken)
            return true;
        // Read before the token, which found nothing new: the next check that counts the same needs no scan.
        m_snapshotWatermark = watermark;
        return false;
    }

    private static string? Watermark(long? counted, Platform.Backup.BackupKey? key) => counted is { } value ? $"{value}|{key?.Id}" : null;

    /// <summary>Commits what changed in any module since the last snapshot (format v7 under .atlas) as one commit by Atlas and pushes it.</summary>
    private async Task SnapshotAsync(GitCommand git, GitWorkingTree tree, GitBackupSettings settings, bool reconcile, CancellationToken ct)
    {
        long since;
        string token;
        long? counted;
        string? watermark;
        // Before the snapshot: it then holds every write the counts include.
        using (var session = m_database.OpenSession())
        {
            using var tx = session.BeginTransaction();
            counted = await m_export.WatermarkAsync(session, ct);
            await tx.CommitAsync(ct);
        }
        using (var session = m_database.OpenSession())
        {
            session.DefaultReadOnly = true;
            using var tx = session.BeginTransaction(IsolationLevel.RepeatableRead);
            await session.CreateSQLQuery("SET TRANSACTION READ ONLY").ExecuteUpdateAsync(ct);
            var key = await m_keys.CurrentAsync(session, ct);
            watermark = Watermark(counted, key);
            token = await m_export.ContentTokenAsync(session, key, ct);
            since = await m_export.WriteAsync(session, new GitInstallationSink(tree), key, reconcile ? null : m_snapshotSince, ct);
            await tx.CommitAsync(ct);
        }
        await tree.CommitAsync("chore: snapshot installation", GitIdentity.Committer, m_clock.GetUtcNow().UtcDateTime, [], ct);
        await PushAsync(git, settings, ct);
        (m_snapshotSince, m_snapshotToken, m_snapshotWatermark, m_snapshotCheckedAt) = (since, token, watermark, m_clock.GetUtcNow());
    }

    /// <summary>A shallow clone of the configured branch into the new <paramref name="folder"/>, for a restore from the repository.</summary>
    public async Task CloneAsync(string folder, CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        if (settings.ProtectedPrivateKey is null || settings.RemoteUrl.Length == 0)
            throw new ApiException(400, "Nejprve nastavte repozitář zálohy, vygenerujte klíč a vložte ho do repozitáře jako deploy klíč.");
        Directory.CreateDirectory(m_dataPath);
        try
        {
            using var ssh = await SshFiles.CreateAsync(m_dataPath, m_deployKeys.Unprotect(settings.ProtectedPrivateKey), settings.KnownHosts, ct);
            var git = new GitCommand(m_dataPath, GitCommand.Environment(m_dataPath, ssh.Command));
            await git.RunGitAsync(["clone", "--quiet", "--depth", "1", "--no-tags", "--branch", settings.Branch, settings.RemoteUrl, folder],
                null, GitCommand.NetworkTimeout, true, ct);
            await SaveKnownHostsAsync(ssh, ct);
        }
        catch (GitException ex)
        {
            throw new ApiException(400, "Repozitář zálohy nejde načíst: " + ex.Message);
        }
    }

    /// <summary>Key, test and key generation folders left behind by a killed process (they hold a private key).</summary>
    private void RemoveStaleFolders()
    {
        if (!Directory.Exists(m_dataPath))
            return;
        foreach (var folder in Directory.EnumerateDirectories(m_dataPath))
        {
            var name = Path.GetFileName(folder);
            if ((name.StartsWith("ssh-", StringComparison.Ordinal) || name.StartsWith("test-", StringComparison.Ordinal) || name.StartsWith("keygen-", StringComparison.Ordinal))
                && Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddHours(-1))
            {
                try { DeleteDirectory(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { m_logger.LogWarning(ex, "Could not remove {Folder}", folder); }
            }
        }
    }

    /// <returns>The failure as the page shows it, null for a good run.</returns>
    private async Task<string?> RunAsync(GitBackupSettings settings, bool reconcile, CancellationToken ct)
    {
        m_running = true;
        m_progress = null;
        try
        {
            using var ssh = await SshFiles.CreateAsync(m_dataPath, m_deployKeys.Unprotect(settings.ProtectedPrivateKey!), settings.KnownHosts, ct);
            var git = new GitCommand(RepositoryPath, GitCommand.Environment(m_dataPath, ssh.Command));
            var tree = new GitWorkingTree(git);
            await PrepareAsync(git, tree, settings, ct);
            await m_sync.RunAsync(tree, async (pages, token) =>
            {
                await PushAsync(git, settings, token);
                await SaveStateAsync(pages, token);
            }, reconcile, (done, total) => m_progress = new GitBackupProgress(done, total), ct);
            await SnapshotAsync(git, tree, settings, reconcile, ct);
            var head = (await git.GitAsync(ct, "rev-parse", "HEAD")).Output.Trim();
            await git.RunGitAsync(["-c", "gc.auto=6700", "gc", "--auto", "--quiet"], null, GitCommand.NetworkTimeout, false, ct);
            await SaveKnownHostsAsync(ssh, ct);
            await FinishAsync(head, null, ct);
            m_reconciled |= reconcile;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            m_logger.LogWarning(ex, "Git backup synchronisation failed");
            var error = SafeError(ex);
            await FinishAsync(null, error, CancellationToken.None);
            return error;
        }
        finally
        {
            m_running = false;
            m_progress = null;
        }
    }

    /// <summary>
    /// Brings the local clone to the remote branch: clones or re-initialises a missing or retargeted cache, drops partial writes
    /// of an interrupted run, fast-forwards or rebases unpushed commits, and refuses a repository this installation did not create.
    /// </summary>
    private async Task PrepareAsync(GitCommand git, GitWorkingTree tree, GitBackupSettings settings, CancellationToken ct)
    {
        var repository = RepositoryPath;
        if (Directory.Exists(Path.Combine(repository, ".git")))
        {
            var origin = await git.RunGitAsync(["config", "--get", "remote.origin.url"], null, null, false, ct);
            var head = await git.RunGitAsync(["symbolic-ref", "--short", "HEAD"], null, null, false, ct);
            if (origin.Output.Trim() != settings.RemoteUrl || head.Output.Trim() != settings.Branch)
                DeleteDirectory(repository);
        }
        if (!Directory.Exists(Path.Combine(repository, ".git")))
        {
            DeleteDirectory(repository);
            Directory.CreateDirectory(repository);
            await git.GitAsync(ct, "init", "--quiet", "--initial-branch=" + settings.Branch);
            await git.GitAsync(ct, "remote", "add", "origin", settings.RemoteUrl);
        }
        // Only this run holds the advisory lock, so any git lock file is left from a killed process.
        foreach (var stale in Directory.EnumerateFiles(Path.Combine(repository, ".git"), "*.lock", SearchOption.AllDirectories))
            File.Delete(stale);
        await git.RunGitAsync(["rebase", "--abort"], null, null, false, ct);
        var hasCommits = (await git.RunGitAsync(["rev-parse", "--verify", "--quiet", "HEAD"], null, null, false, ct)).ExitCode == 0;
        try
        {
            await tree.ResetAsync(hasCommits, ct);
        }
        catch (GitException ex)
        {
            // A damaged cache: start a new clone. Commits it did not push yet are replayed from the database state.
            m_logger.LogWarning(ex, "The git backup clone is damaged; starting a new one");
            DeleteDirectory(repository);
            Directory.CreateDirectory(repository);
            await git.GitAsync(ct, "init", "--quiet", "--initial-branch=" + settings.Branch);
            await git.GitAsync(ct, "remote", "add", "origin", settings.RemoteUrl);
            hasCommits = false;
        }

        var heads = await RemoteHeadsAsync(git, "origin", ct);
        if (heads.Contains(settings.Branch))
        {
            await git.RunGitAsync(["fetch", "--quiet", "--no-tags", "origin", $"+refs/heads/{settings.Branch}:refs/remotes/origin/{settings.Branch}"],
                null, GitCommand.NetworkTimeout, true, ct);
            var remote = $"refs/remotes/origin/{settings.Branch}";
            if (!hasCommits || await IsAncestorAsync(git, "HEAD", remote, ct))
                await git.GitAsync(ct, "reset", "--quiet", "--hard", remote);
            else if (!await IsAncestorAsync(git, remote, "HEAD", ct))
            {
                var rebase = await git.RunGitAsync(["rebase", "--quiet", remote], null, null, false, ct);
                if (rebase.ExitCode != 0)
                {
                    await git.RunGitAsync(["rebase", "--abort"], null, null, false, ct);
                    throw new GitException($"Větev {settings.Branch} ve vzdáleném repozitáři obsahuje cizí změny, které nejdou automaticky sloučit.");
                }
            }
            hasCommits = true;
        }
        else if (heads.Count > 0)
            throw new GitException($"Repozitář není prázdný a nemá větev {settings.Branch} se zálohou Atlasu. Použijte prázdný repozitář.");
        else if (!hasCommits)
        {
            // An empty remote and no local commits: nothing of the recorded state exists any more; replay everything.
            await m_sync.ResetStateAsync(ct);
        }

        if (hasCommits)
        {
            var marker = tree.ReadText(GitBackupLayout.MarkerPath);
            if (marker is null || GitBackupLayout.ReadMarker(marker) != settings.RepositoryId)
                throw new GitException("Repozitář obsahuje jiný obsah než zálohu této wiki. Použijte prázdný repozitář.");
        }
        else
        {
            await tree.WriteTextAsync(GitBackupLayout.ReadmePath, GitBackupLayout.Readme, ct);
            await tree.WriteTextAsync(GitBackupLayout.MarkerPath, GitBackupLayout.Marker(settings.RepositoryId), ct);
            await tree.CommitAsync("docs: initialize Atlas backup", GitIdentity.Committer, DateTime.UtcNow, [], ct);
        }
    }

    private static async Task PushAsync(GitCommand git, GitBackupSettings settings, CancellationToken ct)
    {
        var remote = $"refs/remotes/origin/{settings.Branch}";
        var known = (await git.RunGitAsync(["rev-parse", "--verify", "--quiet", remote], null, null, false, ct)).ExitCode == 0;
        if (known && (await git.GitAsync(ct, "rev-parse", "HEAD")).Output == (await git.GitAsync(ct, "rev-parse", remote)).Output)
            return;
        await git.RunGitAsync(["push", "--quiet", "--porcelain", "origin", $"HEAD:refs/heads/{settings.Branch}"], null, GitCommand.NetworkTimeout, true, ct);
        await git.GitAsync(ct, "update-ref", remote, "HEAD");
    }

    private static async Task<bool> IsAncestorAsync(GitCommand git, string ancestor, string descendant, CancellationToken ct)
        => (await git.RunGitAsync(["merge-base", "--is-ancestor", ancestor, descendant], null, null, false, ct)).ExitCode == 0;

    private static async Task<HashSet<string>> RemoteHeadsAsync(GitCommand git, string remote, CancellationToken ct)
    {
        var result = await git.RunGitAsync(["ls-remote", "--heads", remote], null, GitCommand.NetworkTimeout, true, ct);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split('\t').Last())
            .Where(x => x.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(x => x["refs/heads/".Length..]).ToHashSet(StringComparer.Ordinal);
    }

    private async Task SaveStateAsync(IReadOnlyCollection<GitBackupPage> pages, CancellationToken ct)
    {
        if (pages.Count == 0)
            return;
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        foreach (var page in pages)
            await session.MergeAsync(page, ct);
        await tx.CommitAsync(ct);
    }

    private async Task SaveKnownHostsAsync(SshFiles ssh, CancellationToken ct)
    {
        var known = await ssh.ReadKnownHostsAsync(ct);
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<GitBackupSettings>(1, LockMode.Upgrade, ct);
        settings.KnownHosts = known;
        await tx.CommitAsync(ct);
    }

    private async Task FinishAsync(string? head, string? error, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<GitBackupSettings>(1, LockMode.Upgrade, ct);
        var now = DateTime.UtcNow;
        if (error is null)
        {
            settings.LastSyncAt = now;
            settings.LastCommit = head;
            settings.LastError = null;
            settings.LastErrorAt = null;
        }
        else
        {
            settings.LastError = error;
            settings.LastErrorAt = now;
        }
        await tx.CommitAsync(ct);
    }

    private async Task<GitBackupSettings> LoadAsync(CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<GitBackupSettings>(1, ct);
        await tx.CommitAsync(ct);
        return settings;
    }

    private GitBackupStatus Status(GitBackupSettings settings, int pending)
        => new(new GitBackupSettingsView(settings.Enabled, settings.RemoteUrl, settings.Branch, settings.PublicKey, SshFiles.Fingerprints(settings.KnownHosts)),
            m_running, m_progress, pending, settings.LastSyncAt, settings.LastCommit, settings.LastError, settings.LastErrorAt,
            Warning(settings));

    private string RepositoryPath => Path.Combine(m_dataPath, "repository");

    /// <summary>Local repositories are for tests and a local mirror only (GitBackup:AllowFileRemotes).</summary>
    private bool AllowFileRemotes => m_configuration.GetValue<bool>("GitBackup:AllowFileRemotes");

    /// <summary>ssh://[user@]host[:port]/path, user@host:path (scp form) or, when allowed, file:///path; nothing git would run as a command.</summary>
    internal static string ValidateUrl(string? value, bool required, bool allowFile)
    {
        var url = (value ?? "").Trim();
        if (url.Length == 0)
            return required ? throw new ApiException(400, "Zadejte adresu repozitáře.") : "";
        if (url.Length > 500 || url.StartsWith('-') || url.Any(char.IsControl) || url.Any(char.IsWhiteSpace)
            || !(SshUrl().IsMatch(url) || ScpUrl().IsMatch(url) || allowFile && url.StartsWith("file:///", StringComparison.Ordinal)))
            throw new ApiException(400, "Zadejte adresu repozitáře přes SSH, například ssh://git@git.example.cz/tym/atlas-backup.git nebo git@git.example.cz:tym/atlas-backup.git.");
        return url;
    }

    internal static string ValidateBranch(string? value)
    {
        var branch = (value ?? "").Trim();
        if (branch.Length == 0)
            branch = "main";
        if (!BranchName().IsMatch(branch) || branch.Contains("..") || branch.Contains("//") || branch.EndsWith('/') || branch.EndsWith(".lock", StringComparison.Ordinal)
            || branch.Contains("@{"))
            throw new ApiException(400, "Zadejte platný název větve, například main.");
        return branch;
    }

    private static string Host(string url)
    {
        var ssh = SshUrl().Match(url);
        if (ssh.Success)
            return ssh.Groups["host"].Value.ToLowerInvariant();
        var scp = ScpUrl().Match(url);
        return scp.Success ? scp.Groups["host"].Value.ToLowerInvariant() : "";
    }

    [GeneratedRegex(@"^ssh://(?:[A-Za-z0-9._-]+@)?(?<host>[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\])(?::\d{1,5})?/[^\s]+$")]
    private static partial Regex SshUrl();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+@(?<host>[A-Za-z0-9.-]+):(?!/)[^\s]+$")]
    private static partial Regex ScpUrl();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$")]
    private static partial Regex BranchName();

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        // git marks pack files read-only; Windows refuses to delete those.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static string SafeError(Exception ex) => ex switch
    {
        GitException or ApiException or IOException or UnauthorizedAccessException => ex.Message,
        CryptographicException => "Soubor klíče nelze dešifrovat. Zkontrolujte klíče Data Protection nebo vygenerujte nový klíč.",
        _ => "Podrobnosti jsou v serverovém logu (" + ex.GetType().Name + ")."
    };
}

/// <summary>The deploy key and known hosts as files for one run: private (0600) and deleted afterwards.</summary>
internal sealed class SshFiles : IDisposable
{
    private readonly string m_folder;

    private SshFiles(string folder)
    {
        m_folder = folder;
    }

    public string KeyFile => Path.Combine(m_folder, "id_ed25519");
    public string KnownHostsFile => Path.Combine(m_folder, "known_hosts");
    public string Command => GitCommand.SshCommand(KeyFile, KnownHostsFile);

    public static async Task<SshFiles> CreateAsync(string dataPath, string privateKey, string knownHosts, CancellationToken ct)
    {
        var folder = Path.Combine(dataPath, "ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var files = new SshFiles(folder);
        await WritePrivateAsync(files.KeyFile, privateKey.ReplaceLineEndings("\n"), ct);
        await WritePrivateAsync(files.KnownHostsFile, knownHosts, ct);
        return files;
    }

    private static async Task WritePrivateAsync(string path, string content, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    public async Task<string> ReadKnownHostsAsync(CancellationToken ct) => await File.ReadAllTextAsync(KnownHostsFile, ct);

    /// <summary>Whether a known_hosts text has a key for the host (plain "host" or "[host]:port" entries; ssh -F none never hashes).</summary>
    public static bool Knows(string knownHosts, string host)
        => host.Length > 0 && knownHosts.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split(' ')[0]).SelectMany(x => x.Split(','))
            .Any(x => x.Equals(host, StringComparison.OrdinalIgnoreCase) || x.StartsWith("[" + host + "]:", StringComparison.OrdinalIgnoreCase));

    /// <summary>"ssh-ed25519 SHA256:…" per saved server key, as OpenSSH prints them.</summary>
    public static IReadOnlyList<string> Fingerprints(string knownHosts)
    {
        var result = new List<string>();
        foreach (var line in knownHosts.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || line.StartsWith('#'))
                continue;
            try
            {
                var hash = SHA256.HashData(Convert.FromBase64String(parts[2]));
                result.Add($"{parts[1]} SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}");
            }
            catch (FormatException)
            {
            }
        }
        return result.Distinct().ToList();
    }

    public void Dispose()
    {
        try { Directory.Delete(m_folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
