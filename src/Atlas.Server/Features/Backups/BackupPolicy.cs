using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Backups;

public static class BackupPolicy
{
    public const string NoChanges = "Od poslední úspěšné zálohy se obsah nezměnil. Poslední záloha zůstala zachována.";

    /// <param name="last">The ZIP job's last run.</param>
    /// <param name="gitBackupEnabled">The Git backup is on: then a ZIP archive that is not set up or switched off is no problem; only
    /// neither backup at all is (owner, 29. 10. 2026).</param>
    public static string? Warning(BackupSettings settings, BackupJobRun? last, DateTime now, bool gitBackupEnabled = false)
    {
        if (string.IsNullOrWhiteSpace(settings.Destination))
            return gitBackupEnabled ? null : "Zálohování není nastavené.";
        if (!settings.Enabled)
            return gitBackupEnabled ? null : "Automatické zálohování je vypnuté.";
        if (last?.State == JobStates.Failed)
            return "Poslední zálohování selhalo. Zkontrolujte historii běhů.";
        if (last?.State == JobStates.Succeeded && last.Message?.Contains("Retence:", StringComparison.Ordinal) == true)
            return "Záloha je vytvořená, ale odstranění starších archivů selhalo.";
        if (last?.State == JobStates.Running)
            return now - last.At > TimeSpan.FromHours(2) ? "Zálohování běží neobvykle dlouho." : null;
        if (settings.NextRunAt is null || settings.NextRunAt < now.AddMinutes(-5))
            return "Plánovaná kontrola zálohy neproběhla včas.";
        return null;
    }

    public static IReadOnlyList<BackupRun> Expired(IEnumerable<BackupRun> runs, string destination, int keep)
        => runs.Where(x => x.Status == "succeeded" && x.FileName is not null && x.Destination == destination)
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Skip(Math.Max(1, keep)).ToArray();
}
