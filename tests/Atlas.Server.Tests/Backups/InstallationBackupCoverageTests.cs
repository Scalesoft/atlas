using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Platform.Backup;
using Atlas.Server.Platform.Retention;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>
/// The guard of plan chapter 15.2: every table of the schema is backed up by a module or declared derived, operational or the
/// host's, so a change that adds persistent data without extending a backup fails here. And what the generic restore relies on:
/// the module order follows the references, every Data Protection column travels decrypted inside the secure part.
/// </summary>
[Trait("Category", "Guard")]
public sealed class InstallationBackupCoverageTests
{
    [PostgreSqlFact]
    public async Task Every_table_is_backed_up_by_a_module_or_declared_derived_operational_or_the_hosts()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_backup_coverage_");
        var tables = await TablesAsync(host.ConnectionString);
        var catalog = Catalog();
        var backedUp = catalog.Modules.SelectMany(x => x.Tables.Select(t => t.Name)).ToList();
        var declared = backedUp.Concat(BackupCoverage.Derived).Concat(BackupCoverage.Operational).Concat(BackupCoverage.Host).ToList();

        Assert.Empty(tables.Except(declared).Order(StringComparer.Ordinal));
        Assert.Empty(declared.Except(tables).Order(StringComparer.Ordinal));
        Assert.Empty(declared.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key));
    }

    /// <summary>SEC-T-4, the owner's rule of docs/architecture.md §5.2: no operational table grows without a decision. Each one is aged by
    /// the retention or kept on purpose, with why.</summary>
    [Fact]
    public void Every_operational_table_is_aged_by_the_retention_or_kept_on_purpose()
    {
        var aged = OperationalRetention.Logs.Select(x => x.Table).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(BackupCoverage.Operational.Where(x => !aged.Contains(x) && !OperationalRetention.KeptForever.ContainsKey(x)).Order(StringComparer.Ordinal));
        Assert.Empty(OperationalRetention.KeptForever.Keys.Where(x => aged.Contains(x) || !BackupCoverage.Operational.Contains(x)).Order(StringComparer.Ordinal));
    }

    /// <summary>PER-S-9: the format's specification, by which a backup's completeness is checked, names every module the code backs up.</summary>
    [Fact]
    public void The_format_specification_lists_every_module()
    {
        var specification = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "backup-format-v7.md"));

        Assert.All(Catalog().Modules, module => Assert.Contains($"\n| {module.ModuleId} |", specification, StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task Modules_restore_after_what_they_reference_and_nothing_backed_up_needs_a_row_the_restore_leaves_empty()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_backup_references_");
        var references = await ReferencesAsync(host.ConnectionString);
        var keys = await KeysAsync(host.ConnectionString);
        var catalog = Catalog();
        var owner = catalog.Modules.SelectMany((module, index) => module.Tables.Select(t => (t.Name, Index: index))).ToDictionary(x => x.Name, x => x.Index);
        var problems = new List<string>();
        foreach (var (table, columns, target, deferrable, nullable) in references.Where(x => owner.ContainsKey(x.Table)))
        {
            if (owner.TryGetValue(target, out var targetIndex))
            {
                if (!deferrable && targetIndex > owner[table])
                {
                    problems.Add($"{table}({columns}) -> {target}: the module of {target} restores later");
                }
            }
            else if (!nullable)
            {
                problems.Add($"{table}({columns}) -> {target}: {target} is not backed up and the reference cannot be cleared");
            }
        }
        problems.AddRange(owner.Keys.Where(x => !keys.Contains(x)).Select(x => $"{x}: no primary key to name its rows"));
        Assert.Empty(problems);
    }

    [PostgreSqlFact]
    public async Task Every_data_protection_column_is_declared_and_travels_only_in_the_secure_part()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_backup_protected_");
        var protectedColumns = await ProtectedColumnsAsync(host.ConnectionString);
        var tables = Catalog().Modules.SelectMany(x => x.Tables).ToDictionary(x => x.Name);
        var problems = protectedColumns.Where(x => tables.ContainsKey(x.Table))
            .Where(x => tables[x.Table] is not { Secure: true, Protected: { } declared } || !declared.ContainsKey(x.Column))
            .Select(x => $"{x.Table}.{x.Column}").ToList();
        Assert.Empty(problems);
    }

    internal static BackupCatalog Catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-backup-coverage");
        var environment = new AttachmentTests.EnvironmentStub(root);
        return InstallationModules.Catalog(new EphemeralDataProtectionProvider(), new(new ConfigurationBuilder().Build(), environment),
            new VaultFolders(new ConfigurationBuilder().Build(), environment), NullLogger.Instance);
    }

    private static async Task<List<string>> TablesAsync(string connectionString)
        => (await QueryAsync(connectionString, "SELECT c.relname FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema()) "
            + "AND c.relkind IN ('r', 'p')")).Select(x => (string)x[0]).ToList();

    private static async Task<HashSet<string>> KeysAsync(string connectionString)
        => (await QueryAsync(connectionString, "SELECT c.relname FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid WHERE k.contype = 'p' "
            + "AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema())")).Select(x => (string)x[0]).ToHashSet();

    private static async Task<List<(string Table, string Columns, string Target, bool Deferrable, bool Nullable)>> ReferencesAsync(string connectionString)
        => (await QueryAsync(connectionString,
                "SELECT c.relname, (SELECT string_agg(a.attname, ',') FROM pg_attribute a WHERE a.attrelid = k.conrelid AND a.attnum = ANY(k.conkey)), t.relname, "
                + "k.condeferrable, EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid = k.conrelid AND a.attnum = ANY(k.conkey) AND NOT a.attnotnull) "
                + "FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid JOIN pg_class t ON t.oid = k.confrelid "
                + "WHERE k.contype = 'f' AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema())"))
            .Select(x => ((string)x[0], (string)x[1], (string)x[2], (bool)x[3], (bool)x[4])).ToList();

    private static async Task<List<(string Table, string Column)>> ProtectedColumnsAsync(string connectionString)
        => (await QueryAsync(connectionString, "SELECT c.relname, a.attname FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid "
                + "WHERE c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema()) AND c.relkind = 'r' AND a.attnum > 0 "
                + "AND NOT a.attisdropped AND a.attname LIKE 'Protected%'"))
            .Select(x => ((string)x[0], (string)x[1])).ToList();

    private static async Task<List<object[]>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }
}
