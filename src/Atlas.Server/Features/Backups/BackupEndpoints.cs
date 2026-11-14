using Atlas.Server.Platform.Localization;
using Microsoft.AspNetCore.Mvc;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.Backups.Commands;
using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.Backups.Queries;
using Wolverine;

namespace Atlas.Server.Features.Backups;

public static class BackupEndpoints
{
    private const long MaximumUploadBytes = 1024L * 1024 * 1024;

    public static void MapWikiBackups(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/backups").RequireAuthorization();
        group.MapGet("", async (IMessageBus bus, BackupService backups, GitBackupService git, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            var status = await backups.GetStatusAsync(ct);
            // The administration badge reads this warning; a failing git backup needs the same attention.
            return Results.Ok(status with { Warning = status.Warning ?? await git.WarningAsync(ct) });
        });
        group.MapPut("", async (BackupSettingsRequest request, IMessageBus bus, BackupService backups, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            backups.Validate(request);
            var approval = await bus.InvokeAsync<BackupDestinationApproval>(new AuthorizeBackupDestination(AuthorizeBackupDestinationHandler.ArchivePath,
                AuthorizeBackupDestinationHandler.ArchiveDestination(request), request.Mode, request.Enabled), ct);
            var saved = await backups.SaveSettingsAsync(request, ct, approval);
            await bus.InvokeAsync(new RescheduleWikiBackup(), ct);
            return Results.Ok(saved);
        });
        group.MapPost("/test", async (BackupSettingsRequest request, IMessageBus bus, BackupService backups, HttpContext http, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            try { await backups.TestDestinationAsync(request, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new ApiException(400, "Test přístupu selhal: " + ex.Message);
            }
            return Results.Ok(new { message = ErrorTexts.Translate(RequestLanguage.Of(http), "Přístup ověřen: vytvoření, zápis, čtení, přejmenování a smazání testovacího souboru proběhlo úspěšně.") });
        });
        // „Zálohovat teď“ queues the job's run; the page reads it as the last run, on its own and again while it is queued or running.
        group.MapPost("/run", async (IMessageBus bus, CancellationToken ct) => Results.Ok(new { runId = await bus.InvokeAsync<long>(new RunWikiBackupNow(), ct) }));
        group.MapGet("/run", async (IMessageBus bus, BackupService backups, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            return await backups.LastRunAsync(ct) is { } run ? Results.Ok(run) : Results.NoContent();
        });
        // A complete archive holds the private notes (SEC3-1): the web asks for the fresh sign-in here before it starts the download.
        group.MapPost("/runs/{id:guid}/download/authorize", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new AuthorizeBackupDownload(id, false), ct);
            return Results.NoContent();
        });
        group.MapGet("/runs/{id:guid}/download", async (Guid id, IMessageBus bus, BackupService backups, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            var file = await backups.DownloadAsync(id, ct);
            try
            {
                // After the archive opened, so a download that never starts is not audited as one.
                await bus.InvokeAsync(new AuthorizeBackupDownload(id, true), ct);
            }
            catch
            {
                await file.Stream.DisposeAsync();
                throw;
            }
            return new RevalidatingDownload(Results.File(file.Stream, "application/zip", file.Name), file.Stream,
                async token => (await bus.InvokeAsync<DownloadLease>(new RevalidateBackupDownload(), token)).Until);
        });
        MapGitBackup(group.MapGroup("/git"));
        MapInstallationBackup(group);
        group.MapPost("/import/preview", async (HttpContext context, IMessageBus bus, WikiBackupImport importer, BackupImportGate gate, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            await gate.WaitAsync(ct);
            try
            {
                var (upload, mode, _) = await ReadUploadAsync(context, ct);
                await using var stream = upload.OpenReadStream();
                return Results.Ok(await importer.PreviewAsync(stream, mode, ct));
            }
            catch (InvalidDataException ex) { throw new ApiException(400, "Neplatný archiv zálohy: " + ex.Message); }
            finally { gate.Release(); }
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumUploadBytes + 65536),
            new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumUploadBytes + 65536, ValueLengthLimit = 8192 });
        group.MapPost("/import", async (HttpContext context, IMessageBus bus, WikiBackupImport importer, BackupService backups, BackupImportGate gate, CancellationToken ct) =>
        {
            var actorId = await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            await gate.WaitAsync(ct);
            try
            {
                var (upload, mode, acknowledged) = await ReadUploadAsync(context, ct);
                if (!acknowledged)
                    throw new ApiException(400, "Potvrďte zvolený režim importu a kontrolu náhledu.");
                // Validate the complete archive, its documents included, before even creating a safety backup.
                await using (var preview = upload.OpenReadStream())
                    WikiBackupImport.RequireValidDocuments(await importer.PreviewAsync(preview, mode, ct));
                if (mode == "restore")
                {
                    var safety = await backups.RunAsync("manual", true, ct, safety: true);
                    if (safety?.Status != "succeeded")
                        throw new ApiException(409, "Bezpečnostní záloha před obnovou selhala. Obsah nebyl změněn.");
                }
                await using var stream = upload.OpenReadStream();
                return Results.Ok(await importer.ImportAsync(stream, mode, actorId, ct));
            }
            catch (InvalidDataException ex) { throw new ApiException(400, "Neplatný archiv zálohy: " + ex.Message); }
            finally { gate.Release(); }
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumUploadBytes + 65536),
            new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumUploadBytes + 65536, ValueLengthLimit = 8192 });
    }

    /// <summary>The backup key and the restore of a whole installation from format v7 (a complete ZIP or the Git repository).</summary>
    private static void MapInstallationBackup(RouteGroupBuilder group)
    {
        group.MapGet("/key", async (IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<BackupKeyStatus>(new GetBackupKey(), ct)));
        group.MapPost("/key", async (IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<BackupKeyCreated>(new GenerateBackupKey(), ct)));
        group.MapPost("/restore/preview", async (HttpContext context, IMessageBus bus, InstallationRestore restore, BackupImportGate gate, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            await gate.WaitAsync(ct);
            try
            {
                var (upload, key, _) = await ReadRestoreUploadAsync(context, ct);
                await using var stream = upload.OpenReadStream();
                using var backup = await BackupDirectory.ExtractAsync(stream, key, ct);
                return Results.Ok(await restore.PreviewAsync(backup, ct));
            }
            catch (InvalidDataException ex) { throw new ApiException(400, "Neplatná záloha: " + ex.Message); }
            finally { gate.Release(); }
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumUploadBytes + 65536),
            new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumUploadBytes + 65536, ValueLengthLimit = 8192 });
        group.MapPost("/restore", async (HttpContext context, IMessageBus bus, InstallationRestore restore, BackupService backups, BackupImportGate gate, CancellationToken ct) =>
        {
            var actorId = await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            await gate.WaitAsync(ct);
            try
            {
                var (upload, key, acknowledged) = await ReadRestoreUploadAsync(context, ct);
                await using var stream = upload.OpenReadStream();
                using var backup = await BackupDirectory.ExtractAsync(stream, key, ct);
                return Results.Ok(await RestoreInstallationAsync(backup, acknowledged, actorId, restore, backups, ct));
            }
            catch (InvalidDataException ex) { throw new ApiException(400, "Neplatná záloha: " + ex.Message); }
            finally { gate.Release(); }
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumUploadBytes + 65536),
            new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumUploadBytes + 65536, ValueLengthLimit = 8192 });
        // From the configured repository: no upload limit, the same checks.
        group.MapPost("/git/restore/preview", async (InstallationRestoreRequest request, IMessageBus bus, GitBackupService git, InstallationRestore restore,
            BackupImportGate gate, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            await gate.WaitAsync(ct);
            try
            {
                using var backup = await CloneAsync(git, request.Key, ct);
                return Results.Ok(await restore.PreviewAsync(backup, ct));
            }
            catch (InvalidDataException ex) { throw new ApiException(400, "Neplatná záloha: " + ex.Message); }
            finally { gate.Release(); }
        });
        group.MapPost("/git/restore", async (InstallationRestoreRequest request, IMessageBus bus, GitBackupService git, InstallationRestore restore,
            BackupService backups, BackupImportGate gate, IHostApplicationLifetime lifetime) =>
        {
            // A closed tab or a proxy timeout must not cancel a restore halfway.
            var ct = lifetime.ApplicationStopping;
            var actorId = await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            await gate.WaitAsync(ct);
            try
            {
                using var backup = await CloneAsync(git, request.Key, ct);
                return Results.Ok(await RestoreInstallationAsync(backup, request.Acknowledge, actorId, restore, backups, ct));
            }
            catch (InvalidDataException ex) { throw new ApiException(400, "Neplatná záloha: " + ex.Message); }
            finally { gate.Release(); }
        });
    }

    /// <summary>Refuses what the preview refuses, takes the safety archive of the whole installation, then restores.</summary>
    private static async Task<InstallationRestoreResult> RestoreInstallationAsync(BackupDirectory backup, bool acknowledged, Guid actorId,
        InstallationRestore restore, BackupService backups, CancellationToken ct)
    {
        if (!acknowledged)
            throw new ApiException(400, "Potvrďte obnovu instalace a kontrolu náhledu.");
        var preview = await restore.PreviewAsync(backup, ct);
        if (preview.Problems.Count > 0)
            throw new ApiException(400, preview.Message, "restore_refused", new Dictionary<string, object?> { ["problems"] = preview.Problems });
        var safety = await backups.RunAsync("manual", true, ct, safety: true, safetyMode: "complete");
        if (safety?.Status != "succeeded")
            throw new ApiException(409, "Bezpečnostní záloha před obnovou selhala. Nic nebylo změněno.");
        return await restore.RestoreAsync(backup, actorId, ct);
    }

    private static async Task<BackupDirectory> CloneAsync(GitBackupService git, string? key, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-restore-git-" + Guid.NewGuid().ToString("N"));
        try
        {
            await git.CloneAsync(folder, ct);
            return await BackupDirectory.OpenAsync(folder, key, ct, owned: folder);
        }
        catch
        {
            BackupDirectory.Delete(folder);
            throw;
        }
    }

    private static async Task<(IFormFile Upload, string? Key, bool Acknowledged)> ReadRestoreUploadAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.HasFormContentType)
            throw new ApiException(415, "Nahrajte ZIP jako multipart formulář.");
        var form = await context.Request.ReadFormAsync(ct);
        var upload = form.Files.GetFile("file") ?? throw new ApiException(400, "Chybí ZIP soubor.");
        if (form.Files.Count != 1 || upload.Length is <= 0 or > MaximumUploadBytes)
            throw new ApiException(413, "Nahrajte jeden ZIP o velikosti nejvýše 1 GiB; větší zálohu obnovte z repozitáře Git.");
        var key = form["key"].ToString();
        return (upload, key.Length > 0 ? key : null, form["acknowledge"].ToString() == "true");
    }

    private static void MapGitBackup(RouteGroupBuilder git)
    {
        git.MapGet("", async (IMessageBus bus, GitBackupService service, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            return Results.Ok(await service.GetStatusAsync(ct));
        });
        git.MapPut("", async (GitBackupSettingsRequest request, IMessageBus bus, GitBackupService service, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            var url = service.Validate(request);
            var approval = await bus.InvokeAsync<BackupDestinationApproval>(new AuthorizeBackupDestination(AuthorizeBackupDestinationHandler.GitRemote, url, Enabled: request.Enabled), ct);
            var saved = await service.SaveSettingsAsync(request, ct, approval);
            if (request.Enabled)
                await bus.InvokeAsync(new RequestGitBackup(), ct);
            return Results.Ok(saved);
        });
        git.MapPost("/key", async (IMessageBus bus, GitBackupService service, CancellationToken ct) =>
        {
            var approval = await bus.InvokeAsync<BackupDestinationApproval>(new AuthorizeBackupDestination(AuthorizeBackupDestinationHandler.GitKey, ""), ct);
            return Results.Ok(await service.GenerateKeyAsync(ct, approval));
        });
        git.MapPost("/test", async (GitBackupTestRequest request, IMessageBus bus, GitBackupService service, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            return Results.Ok(new { message = await service.TestAsync(request, ct) });
        });
        git.MapPost("/sync", async (IMessageBus bus, GitBackupService service, IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireBackupAdministrator(), ct);
            // A first run replays the whole history: a closed tab or a proxy timeout must not kill git halfway.
            return Results.Ok(await service.SyncAsync(true, lifetime.ApplicationStopping));
        });
    }

    private static async Task<(IFormFile Upload, string Mode, bool Acknowledged)> ReadUploadAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.HasFormContentType)
            throw new ApiException(415, "Nahrajte ZIP jako multipart formulář.");
        var form = await context.Request.ReadFormAsync(ct);
        var upload = form.Files.GetFile("file") ?? throw new ApiException(400, "Chybí ZIP soubor.");
        if (form.Files.Count != 1 || upload.Length is <= 0 or > MaximumUploadBytes)
            throw new ApiException(413, "Nahrajte jeden ZIP o velikosti nejvýše 1 GiB.");
        var mode = form["mode"].ToString();
        if (mode is not ("add" or "restore"))
            throw new ApiException(400, "Vyberte přidání vedle nebo obnovení podle ID.");
        return (upload, mode, form["acknowledge"].ToString() == "true");
    }
}
