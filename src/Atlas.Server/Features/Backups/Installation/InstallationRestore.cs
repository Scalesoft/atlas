using Npgsql;
using Atlas.Server.Platform.Backup;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Installation;

/// <param name="Mode">"full" with the backup key, "content-only" without it.</param>
/// <param name="Empty">Whether this installation has no content yet; a restore needs it.</param>
public sealed record InstallationRestorePreview(string Mode, string? KeyId, bool Empty, IReadOnlyDictionary<string, int> Rows,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, string Message);

public sealed record InstallationRestoreResult(string Mode, IReadOnlyDictionary<string, int> Rows, IReadOnlyList<string> Warnings, string Message);

/// <summary>
/// Restores a format v7 backup into an empty installation (plan chapter 15.5): every module in dependency order within one
/// transaction (no trigger writes anything on the way in, so every row comes back exactly as it was written), then the data migrations newer than the backup run again (<see cref="InstallationModules.DataMigrations"/>) and the
/// derived data is rebuilt. Without the backup key it restores the content only and says so. The caller
/// authorizes the administrator and creates the safety archive.
/// </summary>
/// <remarks>
/// A service with its own transaction rather than a Wolverine command, like the archive import: it spans
/// files written outside the database that a rollback must remove, and a commit whose failure it has to handle itself.
/// </remarks>
public sealed class InstallationRestore(AtlasDatabase database, BackupCatalog catalog, ILogger<InstallationRestore> logger)
{
    public async Task<InstallationRestorePreview> PreviewAsync(BackupDirectory backup, CancellationToken ct)
    {
        using var session = database.OpenSession();
        session.DefaultReadOnly = true;
        using var transaction = session.BeginTransaction();
        var context = await ValidateAsync(session, backup, ct);
        var empty = await IsEmptyAsync(session, ct);
        await transaction.CommitAsync(ct);
        var problems = empty ? context.Problems : [.. context.Problems, NotEmpty];
        return new InstallationRestorePreview(Mode(context.Mode), backup.Manifest.KeyId, empty, context.Rows, problems, Warnings(context, backup),
            problems.Count > 0 ? "Zálohu nejde obnovit, opravte uvedené problémy." : Describe(context.Mode));
    }

    public async Task<InstallationRestoreResult> RestoreAsync(BackupDirectory backup, Guid actorId, CancellationToken ct)
    {
        // As the schema's owner: the restore empties and refills audit tables the runtime role may only append to (RuntimeDatabaseRole).
        var (owned, ownerConnection) = await database.OpenOwnerSessionAsync(ct);
        await using var connection = ownerConnection;
        using var session = owned;
        using var transaction = session.BeginTransaction();
        RestoreContext? context = null;
        var commitAttempted = false;
        try
        {
            await new AtlasLocks(session).AcquireContentExclusiveAsync(ct);
            var actor = await session.GetAsync<UserAccount>(actorId, ct);
            if (actor is null || !actor.IsActive || !await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct))
            {
                throw new ApiException(403, "Obnovu může provést pouze správce wiki.");
            }
            context = await ValidateAsync(session, backup, ct);
            if (context.Problems.Count > 0)
            {
                throw new ApiException(400, "Zálohu nejde obnovit: " + string.Join(" ", context.Problems.Take(10)));
            }
            if (!await IsEmptyAsync(session, ct))
            {
                throw new ApiException(409, NotEmpty);
            }
            await RestoreTablesAsync(session, context, ct);
            await session.FlushAsync(ct);
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            return new InstallationRestoreResult(Mode(context.Mode), context.Rows, Warnings(context, backup), Restored(context.Mode));
        }
        catch
        {
            if (!commitAttempted)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                RemoveCreatedFiles(context);
            }
            else
            {
                // A lost commit response is ambiguous: keep the files rather than leave committed rows without them.
                logger.LogWarning("Installation restore commit result is uncertain; keeping {Count} restored files", context?.CreatedFiles.Count);
            }
            throw;
        }
    }

    private async Task RestoreTablesAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        await session.CreateSQLQuery("SET LOCAL TimeZone = 'UTC'").ExecuteUpdateAsync(ct);
        await session.CreateSQLQuery("SET CONSTRAINTS ALL DEFERRED").ExecuteUpdateAsync(ct);
        var schema = await TableSchema.ReadAsync(session, ct);
        await ClearAsync(session, schema, context, ct);
        foreach (var module in catalog.Modules)
        {
            await module.RestoreAsync(session, context, ct);
        }
        // Checks the deferred references now that every row is in.
        await session.CreateSQLQuery("SET CONSTRAINTS ALL IMMEDIATE").ExecuteUpdateAsync(ct);
        foreach (var (_, sql) in InstallationModules.DataMigrations.Where(x => x.Schema > context.Reader.Manifest.Schema))
        {
            await session.CreateSQLQuery(sql).ExecuteUpdateAsync(ct);
        }
        foreach (var module in catalog.Modules)
        {
            await module.RebuildAsync(session, ct);
        }
    }

    /// <summary>
    /// Empties every table the restore fills, the derived and operational ones and whatever references them; keeps the host's
    /// tables and the running process's, and without the key the secure tables this installation's own accounts sign in with.
    /// </summary>
    private async Task ClearAsync(ISession session, IReadOnlyDictionary<string, TableInfo> schema, RestoreContext context, CancellationToken ct)
    {
        var kept = new HashSet<string>(BackupCoverage.Host.Concat(schema.Keys.Where(BackupCoverage.Running)), StringComparer.Ordinal);
        if (context.Mode == RestoreMode.ContentOnly)
        {
            kept.UnionWith(catalog.Modules.SelectMany(x => x.Tables).Where(x => x.Secure).Select(x => x.Name));
        }
        var cleared = schema.Keys.Where(x => !kept.Contains(x)).ToHashSet(StringComparer.Ordinal);
        for (var added = true; added;)
        {
            var referencing = kept.Where(x => !BackupCoverage.Host.Contains(x) && schema[x].References.Any(r => cleared.Contains(r.Target))).ToList();
            cleared.UnionWith(referencing);
            kept.ExceptWith(referencing);
            added = referencing.Count > 0;
        }
        foreach (var table in TableSchema.Order(cleared, schema).Reverse())
        {
            try
            {
                await session.CreateSQLQuery($"DELETE FROM {TableSchema.Quote(table)}").ExecuteUpdateAsync(ct);
            }
            catch (Exception ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } postgres)
            {
                throw new ApiException(409, $"Instalace není prázdná: {postgres.TableName} odkazuje na data, která obnova nahradí ({postgres.ConstraintName}). "
                    + "Zrušte tu vazbu (například profil úložiště v nastavení záloh) a obnovu opakujte.");
            }
        }
    }

    private async Task<RestoreContext> ValidateAsync(ISession session, BackupDirectory backup, CancellationToken ct)
    {
        var mode = backup.Key is not null ? RestoreMode.Full : RestoreMode.ContentOnly;
        var restored = catalog.Modules.SelectMany(x => x.Tables).Where(x => !x.Secure || mode == RestoreMode.Full).Select(x => x.Name)
            .Append("UserAccount").ToHashSet(StringComparer.Ordinal);
        var context = new RestoreContext(backup, mode, restored);
        var schemaVersion = await session.CreateSQLQuery("SELECT coalesce(max(\"Version\"), 0) FROM \"SchemaVersion\"").UniqueResultAsync<long>(ct);
        if (backup.Manifest.Schema > schemaVersion)
        {
            context.Problems.Add($"Záloha je z novější verze Atlasu (schéma {backup.Manifest.Schema}, tato instalace {schemaVersion}). Aktualizujte Atlas a obnovu opakujte.");
        }
        foreach (var unknown in backup.Manifest.Modules.Keys.Where(id => catalog.Modules.All(x => x.ModuleId != id)))
        {
            context.Problems.Add($"Záloha obsahuje modul {unknown}, který tato distribuce Atlasu nemá.");
        }
        var known = catalog.Modules.SelectMany(x => new[] { TableBackup.Folder(x.ModuleId, false), TableBackup.Folder(x.ModuleId, true) }).Append(AttachmentBlobsPart.Prefix).ToList();
        context.Problems.AddRange(backup.Manifest.Files.Keys.Where(x => !known.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal)))
            .Take(10).Select(x => $"{x}: soubor modulu, který tato distribuce Atlasu nemá."));
        var schema = await TableSchema.ReadAsync(session, ct);
        var declared = catalog.Modules.SelectMany(x => x.Tables.Select(t => t.Name)).Concat(BackupCoverage.Derived).Concat(BackupCoverage.Operational)
            .Concat(BackupCoverage.Host).ToHashSet(StringComparer.Ordinal);
        context.Problems.AddRange(schema.Keys.Where(x => !declared.Contains(x)).Select(x => $"Tabulka {x} nemá zálohu; obnova by ji vyprázdnila. Jde o chybu verze Atlasu."));
        foreach (var module in catalog.Modules)
        {
            try
            {
                await module.ValidateAsync(session, context, ct);
            }
            catch (InvalidDataException ex)
            {
                context.Problems.Add(ex.Message);
            }
        }
        return context;
    }

    /// <summary>
    /// No content but what the setup and the accounts' sign-ins create: the contact each account gets (AccountContacts.CreateOwnAsync) as it
    /// was created, by the account and naming it, with no other field and never changed since (version 1, not trashed). ClearAsync removes
    /// it with its history; the restart's contacts.accounts job gives the account its contact again from the restored directory.
    /// </summary>
    private static async Task<bool> IsEmptyAsync(ISession session, CancellationToken ct)
        => !await session.CreateSQLQuery("""
            SELECT EXISTS (SELECT 1 FROM "ContentResource" r WHERE (r."TypeId" = 'atlas.contact' AND r."Version" = 1 AND r."TrashedAt" IS NULL
              AND r."Scope" = 'wiki' AND lower(r."Data"->>'directoryId') = CAST(r."CreatedBy" AS text)
              AND r."Data" - ARRAY['name', 'directoryId', 'emails'] = CAST('{}' AS jsonb)
              AND EXISTS (SELECT 1 FROM "UserAccount" a WHERE a."Id" = r."CreatedBy")) IS NOT TRUE)
            """).UniqueResultAsync<bool>(ct);

    private void RemoveCreatedFiles(RestoreContext? context)
    {
        foreach (var file in context?.CreatedFiles ?? [])
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the file {File} of a rolled back restore", file);
            }
        }
    }

    private static IReadOnlyList<string> Warnings(RestoreContext context, BackupDirectory backup)
        => context.Mode == RestoreMode.ContentOnly
            ? [backup.Manifest.KeyId is null
                ? "Záloha nemá zabezpečenou část (vznikla bez klíče zálohy): obnoví se obsah bez účtů, oprávnění a tajemství."
                : $"Bez klíče zálohy ({backup.Manifest.KeyId}) se obnoví jen obsah: účty, skupiny, oprávnění, trezory a tajemství ne.", .. context.Warnings]
            : context.Warnings;

    private static string Mode(RestoreMode mode) => mode == RestoreMode.Full ? "full" : "content-only";

    private const string NotEmpty = "Obnova instalace potřebuje prázdnou instalaci: tato už obsahuje stránky, tabule nebo jiný obsah. "
        + "Obnovte ji do nové instalace, nebo jednotlivé stránky obnovte importem archivu.";

    private static string Describe(RestoreMode mode) => mode == RestoreMode.Full
        ? "Obnoví celou instalaci včetně účtů, skupin, oprávnění, trezorů a nastavení. Účty této instalace nahradí účty ze zálohy."
        : "Obnoví obsah a nastavení bez tajemství. Autoři se obnoví jako neaktivní účty bez přihlášení, oprávnění se neobnoví; váš účet zůstane.";

    private static string Restored(RestoreMode mode) => mode == RestoreMode.Full
        ? "Instalace je obnovená včetně účtů a oprávnění. Restartujte server a přihlaste se účtem ze zálohy."
        : "Obsah je obnovený bez účtů, oprávnění a tajemství. Restartujte server, přidělte oprávnění znovu a zadejte tajemství integrací.";
}
