namespace Atlas.Server.Platform.Backup;

/// <summary>The module backups of the distribution in restore order: every module after the modules it depends on.</summary>
public sealed class BackupCatalog
{
    public BackupCatalog(IEnumerable<IModuleBackup> modules)
    {
        var pending = modules.ToList();
        if (pending.Select(x => x.ModuleId).Distinct(StringComparer.Ordinal).Count() != pending.Count)
        {
            throw new InvalidOperationException("A backup module is declared twice.");
        }
        foreach (var missing in pending.SelectMany(x => x.DependsOn).Where(id => pending.All(x => x.ModuleId != id)))
        {
            throw new InvalidOperationException($"A backup module depends on '{missing}', which the distribution does not contain.");
        }
        var ordered = new List<IModuleBackup>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(x => x.DependsOn.All(id => ordered.Any(done => done.ModuleId == id)))
                ?? throw new InvalidOperationException("Backup modules depend on each other in a cycle: " + string.Join(", ", pending.Select(x => x.ModuleId)));
            ordered.Add(next);
            pending.Remove(next);
        }
        Modules = ordered;
    }

    public IReadOnlyList<IModuleBackup> Modules { get; }
}
