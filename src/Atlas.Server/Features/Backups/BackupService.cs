using Microsoft.AspNetCore.DataProtection;
using NHibernate.Linq;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Backups;

public sealed class BackupService
{
    /// <summary>Also protects the passwords of storage profiles (StorageProfileService): the stored values are interchangeable.</summary>
    public const string DestinationPasswordPurpose = "ScaleAtlas.BackupDestination.Password.v1";
    private readonly AtlasDatabase m_database;
    private readonly WikiBackupArchive m_archive;
    private readonly IDataProtector m_passwords;
    private readonly IDataProtectionProvider m_protection;
    private readonly IWebHostEnvironment m_environment;
    private readonly ILogger<BackupService> m_logger;
    private readonly StorageProfileService m_profiles;
    private readonly InstallationArchive? m_complete;

    /// <param name="complete">Writes the "complete" mode (the whole installation in format v7).</param>
    public BackupService(AtlasDatabase database, WikiBackupArchive archive, IDataProtectionProvider protection,
        IWebHostEnvironment environment, ILogger<BackupService> logger, StorageProfileService? profiles = null,
        InstallationArchive? complete = null)
    {
        m_complete = complete;
        m_database = database;
        m_archive = archive;
        m_passwords = protection.CreateProtector(DestinationPasswordPurpose);
        m_protection = protection;
        m_environment = environment;
        m_logger = logger;
        m_profiles = profiles ?? new StorageProfileService(database, protection, environment);
    }

    public async Task<BackupStatus> GetStatusAsync(CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<BackupSettings>(1, ct);
        // The archives; the run state of each check lives in the job's runs (rows before 3.3 kept their other states, read-only).
        var runs = await session.Query<BackupRun>().Where(x => x.Status == "succeeded").OrderByDescending(x => x.StartedAt).Take(100).ToListAsync(ct);
        var success = await session.Query<BackupRun>().Where(x => x.Status == "succeeded" && x.FileName != null)
            .OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        var last = await LastRunAsync(session, ct);
        var git = await session.GetAsync<Git.GitBackupSettings>(1, ct);
        await tx.CommitAsync(ct);
        var selected = settings.StorageProfileId is { } profileId ? await m_profiles.GetAsync(profileId, ct) : null;
        return new BackupStatus(View(settings, selected?.HasPassword), runs, settings.NextRunAt, success?.FinishedAt,
            BackupPolicy.Warning(settings, last, DateTime.UtcNow, git?.Enabled == true), success);
    }

    /// <summary>The ZIP job's last run, on its own: the page loads it after the first paint and follows it while it is queued or running.</summary>
    public async Task<BackupJobRun?> LastRunAsync(CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var last = await LastRunAsync(session, ct);
        await tx.CommitAsync(ct);
        return last;
    }

    private static async Task<BackupJobRun?> LastRunAsync(NHibernate.ISession session, CancellationToken ct)
        => BackupJobRun.Of(await session.Query<JobRun>().Where(x => x.JobId == ZipBackupJob.JobId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct));

    /// <summary>When the ZIP job is due (<see cref="JobSchedule.FromData"/>): the planned time while scheduling is on.</summary>
    public async Task<DateTime?> NextDueAsync(CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<BackupSettings>(1, ct);
        await tx.CommitAsync(ct);
        return settings.Enabled ? settings.NextRunAt : null;
    }

    /// <param name="approval">The destination changes a fresh sign-in approved, audited with this save (SEC3-4).</param>
    public async Task<BackupStatus> SaveSettingsAsync(BackupSettingsRequest request, CancellationToken ct, Commands.BackupDestinationApproval? approval = null)
    {
        Validate(request);
        var folder = StorageProfilePaths.NormalizeFolder(request.Folder);
        var profile = request.StorageProfileId is { } profileId ? await m_profiles.GetAsync(profileId, ct) : null;
        if (request.Enabled && profile is not null && (!profile.Enabled || !profile.AllowBackups))
            throw new ApiException(400, "Vybrané úložiště je vypnuté nebo není povolené pro zálohy.");
        BackupKeyCreated? created = null;
        using (var session = m_database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var settings = await session.GetAsync<BackupSettings>(1, NHibernate.LockMode.Upgrade, ct);
            if (profile is null && string.IsNullOrEmpty(request.Password) && settings.ProtectedPassword is not null)
                StoredCredential.Require(settings.Destination, settings.Username, settings.Domain, request.Destination.Trim(), request.Username.Trim(), request.Domain.Trim());
            settings.Enabled = request.Enabled;
            settings.IntervalMinutes = request.IntervalMinutes;
            settings.Mode = request.Mode;
            settings.StorageProfileId = request.StorageProfileId;
            settings.Folder = folder;
            settings.Destination = profile is null ? request.Destination.Trim() : StorageProfilePaths.Destination(profile.Destination, folder);
            settings.Username = profile?.Username ?? request.Username.Trim();
            settings.Domain = profile?.Domain ?? request.Domain.Trim();
            settings.RetentionCount = request.RetentionCount;
            if (profile is null && !string.IsNullOrEmpty(request.Password))
                settings.ProtectedPassword = m_passwords.Protect(request.Password);
            // A changed configuration takes effect without a server restart.
            settings.NextRunAt = request.Enabled ? DateTime.UtcNow : null;
            // The complete mode encrypts its secure part with the backup key; this response is the one time a new key is shown.
            if (approval is not null)
                await approval.WriteAsync(session, ct);
            if (request.Enabled && request.Mode == "complete" && settings.ProtectedBackupKey is null)
            {
                var key = new BackupKeys(m_protection).Replace(settings);
                await Commands.BackupDestinationApproval.WriteKeyAsync(approval, session, key.Id, ct);
                created = new BackupKeyCreated(key.Text, key.Id, settings.BackupKeyCreatedAt!.Value);
            }
            await tx.CommitAsync(ct);
        }
        return await GetStatusAsync(ct) with { NewBackupKey = created };
    }

    public async Task TestDestinationAsync(BackupSettingsRequest request, CancellationToken ct)
    {
        Validate(request);
        if (request.StorageProfileId is { } profileId)
        {
            using var selected = await m_profiles.OpenAsync(profileId, request.Folder, true, ct);
            await selected.TestAsync(ct);
            return;
        }
        var settings = await LoadSettingsAsync(ct);
        if (string.IsNullOrEmpty(request.Password) && settings.ProtectedPassword is not null)
            StoredCredential.Require(settings.Destination, settings.Username, settings.Domain, request.Destination.Trim(), request.Username.Trim(), request.Domain.Trim());
        var password = string.IsNullOrEmpty(request.Password) ? Password(settings) : request.Password;
        using var destination = FileStorages.Open(new(request.Destination.Trim(), request.Username.Trim(), request.Domain.Trim(), password));
        await destination.TestAsync(ct);
    }

    /// <summary>One check of the ZIP backup: the job's run (<see cref="ZipBackupJob"/>) or the safety archive before an import or a
    /// restore. Only a new archive is stored (<see cref="BackupRun"/>, for the download and retention); a skipped or failed check is
    /// returned unsaved, its state is the job's run.</summary>
    /// <returns>Null when a scheduled check is not due.</returns>
    /// <param name="safetyMode">The mode of a safety archive: "full" before an archive import, "complete" before an installation restore.</param>
    public async Task<BackupRun?> RunAsync(string trigger, bool force, CancellationToken ct, bool safety = false, string safetyMode = "full")
    {
        if (trigger is not ("manual" or "scheduled"))
            throw new ArgumentException("Unknown backup trigger.", nameof(trigger));

        var settings = await LoadSettingsAsync(ct);
        var now = DateTime.UtcNow;
        if (trigger == "scheduled" && (!settings.Enabled || settings.NextRunAt > now))
            return null;
        if (safety)
        {
            settings.Mode = safetyMode;
            if (string.IsNullOrWhiteSpace(settings.Destination))
                settings.Destination = Path.Combine(m_environment.ContentRootPath, ".data", "backup-safety");
        }
        if (string.IsNullOrWhiteSpace(settings.Destination))
            throw new ApiException(400, "Nejdříve nastavte cílové úložiště záloh.");
        var run = new BackupRun
        {
            StartedAt = now, Trigger = trigger, Mode = settings.Mode, Destination = settings.Destination,
            Message = safety ? "Bezpečnostní záloha před obnovou." : "Zálohování probíhá."
        };
        await StartRunAsync(run, settings.IntervalMinutes, ct);
        return await ExecuteRunAsync(run, settings, force, ct);
    }

    private async Task<BackupRun> ExecuteRunAsync(BackupRun run, BackupSettings settings, bool force, CancellationToken ct)
    {
        try
        {
            using var destination = await OpenDestinationAsync(settings, ct);
            var complete = settings.Mode == "complete" ? m_complete ?? throw new InvalidOperationException("The complete backup is not registered.") : null;
            var token = complete is not null ? await complete.GetContentTokenAsync(ct) : await m_archive.GetContentTokenAsync(settings.Mode, ct);
            var previous = await LastArchiveAsync(settings, ct);
            if (!force && previous?.ContentToken == token && previous.FileName is not null && await destination.ExistsAsync(previous.FileName, ct))
            {
                run.Status = "skipped";
                run.ContentToken = token;
                run.Message = BackupPolicy.NoChanges;
            }
            else
            {
                using var archive = complete is not null ? await complete.CreateAsync(ct) : await m_archive.CreateAsync(settings.Mode, ct);
                run.ContentToken = archive.ContentToken;
                var name = $"wiki-{run.StartedAt:yyyyMMdd-HHmmss}-{settings.Mode}-{run.Id:N}.zip";
                await using var input = new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await destination.PublishNewAsync(name, input, ct);
                run.Size = input.Length;
                run.FileName = name;
                run.Status = "succeeded";
                run.Message = "Záloha byla úspěšně vytvořena.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = "failed";
            run.Message = "Zálohování bylo přerušeno. Předchozí záloha zůstala zachována.";
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.Message = "Záloha selhala. " + SafeError(ex);
            m_logger.LogError(ex, "Backup {BackupId} failed", run.Id);
        }
        run.FinishedAt = DateTime.UtcNow;
        // A skipped or failed check stores no archive and never ages/deletes the last usable one.
        if (run.Status == "succeeded")
        {
            await SaveRunAsync(run, CancellationToken.None);
            await ApplyRetentionAsync(run, settings, ct);
        }
        return run;
    }

    private async Task ApplyRetentionAsync(BackupRun run, BackupSettings settings, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<BackupRun> old;
            using (var session = m_database.OpenSession())
            using (var tx = session.BeginTransaction())
            {
                old = await session.Query<BackupRun>()
                    .Where(x => x.Status == "succeeded" && x.FileName != null && x.Destination == settings.Destination)
                    .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
                    .Skip(settings.RetentionCount).ToListAsync(ct);
                await tx.CommitAsync(ct);
            }
            using var destination = await OpenDestinationAsync(settings, ct);
            foreach (var expired in old)
            {
                await destination.DeleteAsync(expired.FileName!, ct);
                expired.FileName = null;
                expired.Message += " Archiv odstraněn podle nastavené retence.";
                await SaveRunAsync(expired, ct);
            }
        }
        catch (Exception ex)
        {
            run.Message += " Retence: starší archivy se nepodařilo odstranit. " + SafeError(ex);
            await SaveRunAsync(run, CancellationToken.None);
            m_logger.LogWarning(ex, "Backup retention failed after {BackupId}", run.Id);
        }
    }

    public async Task<(Stream Stream, string Name)> DownloadAsync(Guid id, CancellationToken ct)
    {
        BackupRun run;
        using (var session = m_database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            run = await session.GetAsync<BackupRun>(id, ct) ?? throw new ApiException(404, "Záloha nebyla nalezena.");
            await tx.CommitAsync(ct);
        }
        if (run.Status != "succeeded" || run.FileName is null)
            throw new ApiException(410, "Archiv není dostupný; zkontrolujte historii retence.");
        var settings = await LoadSettingsAsync(ct);
        if (run.Destination != settings.Destination && run.Destination.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(409, "Pro stažení ze staršího SMB umístění nejprve nastavte jeho připojení.");
        using var destination = run.Destination == settings.Destination
            ? await OpenDestinationAsync(settings, ct)
            : FileStorages.Open(new(run.Destination, settings.Username, settings.Domain, Password(settings)));
        return (await destination.OpenReadAsync(run.FileName, ct), run.FileName);
    }

    private async Task<BackupSettings> LoadSettingsAsync(CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<BackupSettings>(1, ct);
        await tx.CommitAsync(ct);
        if (settings.StorageProfileId is { } profileId)
        {
            var profile = await m_profiles.GetAsync(profileId, ct);
            settings.StorageProfileVersion = profile.Version;
            settings.Destination = StorageProfilePaths.Destination(profile.Destination, settings.Folder);
            settings.Username = profile.Username;
            settings.Domain = profile.Domain;
        }
        return settings;
    }

    private async Task<BackupRun?> LastArchiveAsync(BackupSettings settings, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var result = await session.Query<BackupRun>().Where(x => x.Status == "succeeded" && x.FileName != null
                && x.Mode == settings.Mode && x.Destination == settings.Destination)
            .OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task StartRunAsync(BackupRun run, int intervalMinutes, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        var settings = await session.GetAsync<BackupSettings>(1, NHibernate.LockMode.Upgrade, ct);
        settings.NextRunAt = settings.Enabled ? run.StartedAt.AddMinutes(intervalMinutes) : null;
        await tx.CommitAsync(ct);
    }

    private async Task SaveRunAsync(BackupRun run, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var tx = session.BeginTransaction();
        await session.MergeAsync(run, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The 400 of a request, before its destination is authorized (AuthorizeBackupDestination).</summary>
    public void Validate(BackupSettingsRequest request)
    {
        try { ValidateDestination(request); }
        catch (ArgumentException ex) { throw new ApiException(400, "Neplatné cílové úložiště: " + ex.Message); }
    }

    private void ValidateDestination(BackupSettingsRequest request)
    {
        ValidateSchedule(request);
        StorageProfilePaths.NormalizeFolder(request.Folder);
        if (request.StorageProfileId.HasValue) return;
        var normalized = request.Destination.Trim();
        if (normalized.Length == 0) return;
        using var destination = FileStorages.Open(new(normalized, request.Username, request.Domain, request.Password));
        if (!normalized.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            ValidateLocalDirectory(normalized);
    }

    private static void ValidateSchedule(BackupSettingsRequest request)
    {
        if (request.IntervalMinutes is < 5 or > 525600 || request.RetentionCount is < 1 or > 1000 || request.Mode is not ("full" or "latest" or "complete"))
            throw new ApiException(400, "Zadejte interval 5–525600 minut, retenci 1–1000 archivů a režim FULL, LATEST nebo COMPLETE.");
        if (!request.StorageProfileId.HasValue && (request.Destination is null || request.Username is null || request.Domain is null))
            throw new ApiException(400, "Neplatné nastavení cílového úložiště.");
        if (request.Enabled && !request.StorageProfileId.HasValue && string.IsNullOrWhiteSpace(request.Destination))
            throw new ApiException(400, "Pro zapnutí zálohování vyberte cílové úložiště.");
    }

    private void ValidateLocalDirectory(string normalized)
    {
        // Judged where the archives really land: a junction on the way is fine, one that leads into wwwroot is not.
        var web = StorageProfilePaths.RealPath(m_environment.WebRootPath ?? Path.Combine(m_environment.ContentRootPath, "wwwroot"));
        if (StorageProfilePaths.IsWithin(StorageProfilePaths.RealPath(normalized), web))
            throw new ApiException(400, "Zálohy nesmí být ve veřejné složce wwwroot.");
    }

    private async Task<IFileStorage> OpenDestinationAsync(BackupSettings settings, CancellationToken ct)
        => settings.StorageProfileId is { } profileId
            ? await m_profiles.OpenAsync(profileId, settings.Folder, true, settings.StorageProfileVersion!.Value, ct)
            : FileStorages.Open(new(settings.Destination, settings.Username, settings.Domain, Password(settings)));

    private string? Password(BackupSettings settings)
        => settings.ProtectedPassword is null ? null : m_passwords.Unprotect(settings.ProtectedPassword);
    private static BackupSettingsView View(BackupSettings settings, bool? hasPassword = null)
        => new(settings.Enabled, settings.IntervalMinutes, settings.Mode, settings.Destination, settings.Username,
            settings.Domain, settings.RetentionCount, hasPassword ?? settings.ProtectedPassword is not null, settings.StorageProfileId, settings.Folder);
    private static string SafeError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ApiException ? ex.Message : "Podrobnosti jsou v serverovém logu (" + ex.GetType().Name + ").";
}
