using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.Pages.Live;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Storage;
using NHibernate;
using Npgsql;
using ISession = NHibernate.ISession;
namespace Atlas.Server.Features.PagePdfExports;

/// <summary>Saved revisions are durable pending work. Rendering and storage I/O never hold a page transaction.</summary>
public sealed class PagePdfExportRunner(AtlasDatabase database, IServiceScopeFactory scopes, IConfiguration configuration,
    StorageProfileService profiles, IContentPdfRenderer renderer, ILogger<PagePdfExportRunner> logger, OidcRuntimeConfiguration? oidc = null,
    LiveRelay? relay = null)
{
    /// <summary>The exports one pass runs at most.</summary>
    public const int Batch = 8;

    /// <summary>The enabled exports of live pages whose revision, configuration or storage profile has not been exported yet.</summary>
    private const string Pending = """
        FROM "PagePdfExport" e JOIN "Page" p ON p."Id"=e."PageId"
        JOIN "StorageProfile" sp ON sp."Id"=e."StorageProfileId"
        JOIN "PageRevision" r ON r."PageId"=p."Id" AND
            ((e."PublishedOnly" AND r."Id"=p."PublishedRevisionId") OR (NOT e."PublishedOnly" AND r."ActiveTo" IS NULL))
        LEFT JOIN "PagePublication" pub ON pub."PageId"=p."Id" AND pub."ActiveTo" IS NULL
        WHERE e."Enabled" AND NOT p."IsTrashed"
          AND (e."LastRevisionId" IS DISTINCT FROM r."Id" OR e."LastConfigurationVersion"<>e."Version" OR e."LastProfileVersion"<>sp."Version")
        """;

    /// <summary>Runs up to <see cref="Batch"/> due exports; returns how many were due.</summary>
    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct)
    {
        using var session = database.OpenSession();
        // Bounded work per pass, selecting only pending revisions/configurations so current pages cannot starve others.
        var ids = await session.CreateSQLQuery($"""
            SELECT e."PageId" {Pending}
              AND e."NextAttemptAt"<=:now
              AND (e."RunNowRevisionId"=r."Id" OR GREATEST(r."ActiveFrom", CASE WHEN e."PublishedOnly" THEN pub."ActiveFrom" ELSE r."ActiveFrom" END)<=:before)
            ORDER BY e."NextAttemptAt",e."PageId" LIMIT {Batch}
            """).SetParameter("now", now).SetParameter("before", now.AddSeconds(-30)).ListAsync<Guid>(ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await RunOneAsync(id, now, ct);
        }
        return ids.Count;
    }

    /// <summary>When <see cref="RunDueAsync"/> next finds an export: the pending one whose backoff ends and revision settles first
    /// (30 s after its save or publication, at once for "Exportovat teď"); null when none is pending.</summary>
    public async Task<DateTime?> NextDueAsync(CancellationToken ct)
    {
        using var session = database.OpenSession();
        return await session.CreateSQLQuery($"""
            SELECT min(GREATEST(e."NextAttemptAt", CASE WHEN e."RunNowRevisionId"=r."Id" THEN e."NextAttemptAt"
                ELSE GREATEST(r."ActiveFrom", CASE WHEN e."PublishedOnly" THEN pub."ActiveFrom" ELSE r."ActiveFrom" END) + interval '30 seconds' END)) AS due
            {Pending}
            """).AddScalar("due", NHibernateUtil.UtcDateTime).UniqueResultAsync<DateTime?>(ct);
    }
    private async Task RunOneAsync(Guid pageId, DateTime now, CancellationToken stoppingToken)
    {
        // The render slot before the snapshot is built: while another export prints or an interactive one waits for it, the rule
        // stays due and runs at a later tick, with no error recorded.
        using var slot = renderer.TryReserveAutomatic();
        if (slot is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        // Session locks on an unpooled connection disappear on process death. No long page/actor row locks.
        var options = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("atlas-db")) { Pooling = false };
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(current_schema() || ':page-pdf:' || @id, 0))", connection);
        claim.Parameters.AddWithValue("id", pageId.ToString());
        if (await claim.ExecuteScalarAsync(ct) is not true) return;
        Snapshot? work = null;
        int? attemptedVersion = null;
        try
        {
            work = await BuildAsync(pageId, now, version => attemptedVersion = version, ct);
            if (work is null) return;
            await using var archive = work.Archive;
            ContentExportArchive rendered;
            // Released as soon as the PDF exists: an interactive export does not wait for the storage upload.
            try { rendered = await slot.RenderAsync(archive, work.Origin, PdfCoverDetails.Parse(work.CoverDetails), ct); }
            finally { slot.Dispose(); }
            await using var pdf = rendered;
            if (!await StillAllowedAsync(work, ct)) return;
            // Detect a lost lock connection before publishing; release is by unpooled connection disposal.
            await using var check = new NpgsqlCommand("SELECT 1", connection);
            await check.ExecuteScalarAsync(ct);
            using var storage = await profiles.OpenAsync(work.ProfileId, work.Folder, false, work.ProfileVersion, ct);
            if (!await StillAllowedAsync(work, ct)) return;
            async Task BeforeCommit(CancellationToken token)
            {
                if (!await StillAllowedAsync(work, token)) throw new SupersededExportException();
                await using var lockCheck = new NpgsqlCommand("SELECT 1", connection);
                await lockCheck.ExecuteScalarAsync(token);
            }
            // Every PDF carries the export's date and time; with "keep versions" each one stays, otherwise the previous one is removed below.
            var written = work with { FileName = DatedName(work.FileName, now) };
            await storage.PublishReplaceAsync(written.FileName, pdf.Stream, ct, BeforeCommit);
            if (!work.KeepVersions) await RemovePreviousAsync(written, storage, ct);
            await RecordAsync(work.PageId, work.ConfigurationVersion, work.RevisionId, now, null, stoppingToken, work.ProfileVersion, written);
        }
        catch (SupersededExportException)
        {
            logger.LogDebug("Automatic PDF export for page {PageId} was superseded before publication", pageId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (ApiException busy) when (busy.Code == ContentPdfRenderer.BusyCode)
        {
            logger.LogDebug("Automatic PDF export for page {PageId} waits for the renderer", pageId);
        }
        catch (Exception error)
        {
            // Do not return provider errors/credentials to the page; diagnostic details remain in server logs.
            logger.LogWarning(error, "Automatic PDF export failed for page {PageId}", pageId);
            var message = error is NameTakenException
                ? "Soubor s tímto názvem PDF v této složce vytváří export jiné stránky. Zvolte jiný název PDF nebo složku."
                : error is ApiException api && api.Status is 403 or 404
                ? "Export nemá oprávnění ke stránce nebo úložišti. Zkontrolujte nastavení."
                : error is ApiException content && content.Status == 422
                    ? "Obsah stránky nelze připravit pro PDF. Zkontrolujte obrázky a obsahové moduly."
                    : "PDF se nepodařilo uložit. Export se automaticky zopakuje.";
            await RecordAsync(pageId, work?.ConfigurationVersion ?? attemptedVersion, null, now, message, stoppingToken);
        }
    }
    private async Task<Snapshot?> BuildAsync(Guid pageId, DateTime now, Action<int> captureVersion, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<ISession>();
        using var tx = session.BeginTransaction();
        var rule = await session.GetAsync<PagePdfExport>(pageId, ct);
        if (rule is null || !rule.Enabled || rule.NextAttemptAt > now) return null;
        captureVersion(rule.Version);
        var (page, owner, profile) = await AuthorizeAsync(session, rule, ct);
        var revision = await PagePdfExportHandler.DesiredRevisionAsync(session, page, rule.PublishedOnly, ct);
        if (revision is null || rule.RunNowRevisionId != revision.Id && revision.ActiveFrom > now.AddSeconds(-30)
            || revision.Id == rule.LastRevisionId && rule.Version == rule.LastConfigurationVersion && profile.Version == rule.LastProfileVersion) return null;
        // 3.6 repair on open: the export names the revision, so the handler's own hook (current revision only) does not run.
        if (await scope.ServiceProvider.GetRequiredService<Pages.Domain.RequirementPageConversion>().RepairOnOpenAsync(pageId, revision.Id, revision.ContentJson, ct))
        {
            await session.RefreshAsync(revision, ct);
        }
        var archive = await scope.ServiceProvider.GetRequiredService<BuildContentExportHandler>()
            .BuildForActorAsync(new(pageId, false, revision.Id), owner, ct, OutboundAudience.ExternalFile);
        try
        {
            await tx.CommitAsync(ct);
            return new(pageId, rule.StorageProfileId, profile.Version, rule.Version, revision.Id, revision.Version,
                // The configured public origin, else none (Atlas links stay relative): a rule's stored origin may come from a Host header (SEC-I-3).
                rule.Folder, rule.FileName, rule.KeepVersions, oidc?.PublicOrigin is { } origin ? origin + "/" : "", rule.CoverDetails, archive);
        }
        catch { await archive.DisposeAsync(); throw; }
    }
    private async Task<bool> StillAllowedAsync(Snapshot work, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var rule = await session.GetAsync<PagePdfExport>(work.PageId, ct);
        if (rule is null || !rule.Enabled || rule.Version != work.ConfigurationVersion) return false;
        var (page, _, profile) = await AuthorizeAsync(session, rule, ct);
        var desired = await PagePdfExportHandler.DesiredRevisionAsync(session, page, rule.PublishedOnly, ct);
        if (profile.Version != work.ProfileVersion || desired?.Id != work.RevisionId) return false;
        // The settings refuse a name another page's export uses; this guards a race and rows from before that rule.
        if (await PagePdfExportHandler.ClaimedByOtherAsync(session, work.PageId, work.ProfileId, work.Folder, work.FileName, ct)) throw new NameTakenException();
        return true;
    }
    /// <summary>
    /// The page keeps one PDF: the file it last wrote under another name or folder goes once the new one is published, unless another
    /// page's export has taken that name since. A failed delete retries the whole export. Case-only renames keep the file (case-insensitive
    /// folders would lose the new PDF). shortcut: a PDF left in a previous storage profile stays there, as before; clean it up there.
    /// </summary>
    private async Task RemovePreviousAsync(Snapshot work, IFileStorage storage, CancellationToken ct)
    {
        string folder, name;
        using (var session = database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var rule = await session.GetAsync<PagePdfExport>(work.PageId, ct);
            if (rule is not { LastFolder: { } lastFolder, LastFileName: { } lastName } || rule.LastStorageProfileId != work.ProfileId
                || string.Equals(lastFolder, work.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(lastName, work.FileName, StringComparison.OrdinalIgnoreCase)
                || await PagePdfExportHandler.ClaimedByOtherAsync(session, work.PageId, work.ProfileId, lastFolder, lastName, ct)) return;
            (folder, name) = (lastFolder, lastName);
        }
        if (string.Equals(folder, work.Folder, StringComparison.OrdinalIgnoreCase)) { await storage.DeleteAsync(name, ct); return; }
        using var previous = await profiles.OpenAsync(work.ProfileId, folder, false, work.ProfileVersion, ct);
        await previous.DeleteAsync(name, ct);
    }
    private async Task<(Page, UserAccount, StorageProfile)> AuthorizeAsync(ISession session, PagePdfExport rule, CancellationToken ct)
    {
        var page = await session.GetAsync<Page>(rule.PageId, ct);
        var owner = await session.GetAsync<UserAccount>(rule.OwnerId, ct);
        if (page is null || page.IsTrashed || owner is null || !owner.IsActive) throw new ApiException(403, "Export není povolen.");
        if (!(await new ResourceGrantSnapshots(session).AuthorizePageAsync(owner, page, AccessOperation.EditContent, ct)).Allowed)
            throw new ApiException(403, "Export není povolen.");
        var profile = await profiles.RequireAvailableAsync(session, rule.StorageProfileId, page.SpaceId, ct);
        return (page, owner, profile);
    }
    private async Task RecordAsync(Guid pageId, int? configurationVersion, Guid? revisionId, DateTime now, string? error, CancellationToken ct, int profileVersion = 0,
        Snapshot? published = null)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var rule = await session.GetAsync<PagePdfExport>(pageId, LockMode.Upgrade, ct);
        if (rule is null) return;
        // What is in storage, even when the settings changed meanwhile: the next export removes it and other pages cannot take its name.
        if (published is not null) (rule.LastStorageProfileId, rule.LastFolder, rule.LastFileName) = (published.ProfileId, published.Folder, published.FileName);
        if (configurationVersion is not { } version || rule.Version != version) { await tx.CommitAsync(ct); return; }
        if (error is null)
        {
            rule.LastProfileVersion = profileVersion; rule.LastRevisionId = revisionId; rule.LastConfigurationVersion = rule.Version; rule.LastExportedAt = now;
            rule.LastError = ""; rule.FailureCount = 0; rule.RunNowRevisionId = null; rule.NextAttemptAt = now;
        }
        else
        {
            rule.LastError = error; rule.FailureCount = Math.Min(rule.FailureCount + 1, 10);
            rule.NextAttemptAt = now.AddSeconds(Math.Min(300, 5 * (1 << rule.FailureCount)));
        }
        await tx.CommitAsync(ct);
        if (relay is null) return;
        try
        {
            await PagePdfExportChanged.PublishAsync(relay, pageId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The live signal of the PDF export of {PageId} could not be sent", pageId);
        }
    }
    /// <summary>"Name.pdf" becomes "Name 2026-11-08 14-30.pdf" (Prague time, no colon: valid on SMB and Windows). Two exports in one minute
    /// share a name and the later replaces the earlier.</summary>
    public static string DatedName(string fileName, DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"));
        return $"{Path.GetFileNameWithoutExtension(fileName)} {local:yyyy-MM-dd HH-mm}.pdf";
    }
    private static readonly System.Text.RegularExpressions.Regex s_dated = new(@" \d{4}-\d\d-\d\d \d\d-\d\d\.pdf$");
    /// <summary>The name a <see cref="DatedName"/> was written for ("Name 2026-11-08 14-30.pdf" is "Name.pdf"); any other name stays.</summary>
    public static string UndatedName(string fileName) => s_dated.Replace(fileName, ".pdf");
    private sealed class SupersededExportException : Exception;
    private sealed class NameTakenException : Exception;
    private sealed record Snapshot(Guid PageId, Guid ProfileId, int ProfileVersion, int ConfigurationVersion, Guid RevisionId,
        int RevisionVersion, string Folder, string FileName, bool KeepVersions, string Origin, string CoverDetails, ContentExportArchive Archive);
}

/// <summary>Server → client on <c>page:{id}</c>, to its editors: the automatic PDF export of the page stored a file or failed; read its state
/// again. Editors are the page channel's writers, decided as EditContent (PageObjectType's CanEdit), the operation the export's HTTP API
/// requires (PagePdfExportHandler.RequirePageAsync); a reader of the page hears nothing.</summary>
public sealed record PagePdfExportChanged(Guid PageId)
{
    public const string Event = "PdfExportChanged";

    public static Task PublishAsync(LiveRelay relay, Guid pageId)
        => relay.PublishAsync(PageLiveChannel.Name(pageId), Event, new PagePdfExportChanged(pageId), writersOnly: true);
}
