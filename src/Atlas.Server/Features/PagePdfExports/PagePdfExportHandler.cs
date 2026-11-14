using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Storage;
using NHibernate;
using NHibernate.Linq;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.PagePdfExports.Commands;
using Atlas.Server.Features.PagePdfExports.Queries;
namespace Atlas.Server.Features.PagePdfExports;

public sealed class PagePdfExportHandler(ISession session, IContentAccess access, IAtlasLocks locks, StorageProfileService profiles, ModuleSettingsAccess administrators,
    IJobRequests jobs)
{
    public async Task<PagePdfExportView> Handle(GetPagePdfExport query, CancellationToken ct)
    {
        var (page, _) = await RequirePageAsync(query.PageId, ct);
        return await ViewAsync(page, await session.GetAsync<PagePdfExport>(page.Id, ct), ct);
    }
    public async Task<PagePdfExportView> Handle(SavePagePdfExport command, CancellationToken ct)
    {
        var (page, actor) = await RequirePageAsync(command.PageId, ct);
        // Serialize settings edits, never held by the renderer or remote storage writes. The held page is re-read under the lock.
        await session.GetForUpdateAsync<Page>(page.Id, ct);
        var rule = await session.GetAsync<PagePdfExport>(page.Id, ct);
        var request = command.Request;
        Rules.ExpectVersion(rule?.Version ?? 0, request.ExpectedVersion);
        if (rule is null && !request.Enabled && request.StorageProfileId is null) return await ViewAsync(page, null, ct);
        var (profileId, folder, name) = await TargetAsync(page, rule, request, ct);
        var coverDetails = request.CoverDetails is null ? rule?.CoverDetails ?? PdfCoverDetails.Default : PdfCoverDetails.Join(request.CoverDetails);
        await locks.AcquireAdvisoryAsync(731145102, ct);
        if (await ClaimedByOtherAsync(session, page.Id, profileId, folder, name, ct))
            throw new ApiException(409, "Soubor s tímto názvem PDF v této složce již vytváří export jiné stránky. Zvolte jiný název PDF nebo složku.");
        var isNew = rule is null;
        rule ??= new PagePdfExport { PageId = page.Id };
        rule.OwnerId = actor.Id; rule.StorageProfileId = profileId; rule.Enabled = request.Enabled;
        rule.Folder = folder; rule.FileName = name; rule.KeepVersions = request.KeepVersions; rule.PublishedOnly = request.PublishedOnly; rule.CoverDetails = coverDetails;
        rule.SourceOrigin = command.SourceOrigin; rule.Version = isNew ? 1 : rule.Version + 1;
        rule.LastError = ""; rule.FailureCount = 0; rule.NextAttemptAt = DateTime.UtcNow + PagePdfExportJob.Settle; rule.RunNowRevisionId = null;
        if (isNew) await session.SaveAsync(rule, ct);
        // The plan, not a run waiting for NextAttemptAt: "Exportovat teď" would merge into that run and wait for it.
        if (rule.Enabled) await jobs.RescheduleAsync(PagePdfExportJob.JobId, ct);
        return await ViewAsync(page, rule, ct);
    }
    public async Task<PagePdfExportView> Handle(RunPagePdfExport command, CancellationToken ct)
    {
        var (page, actor) = await RequirePageAsync(command.PageId, ct);
        await session.GetForUpdateAsync<Page>(page.Id, ct);
        var rule = await session.GetAsync<PagePdfExport>(page.Id, LockMode.Upgrade, ct) ?? throw new ApiException(400, "Nejdříve uložte nastavení exportu.");
        if (!rule.Enabled) throw new ApiException(400, "Automatický export je vypnutý.");
        await profiles.RequireAvailableAsync(session, rule.StorageProfileId, page.SpaceId, ct);
        rule.OwnerId = actor.Id; rule.Version++; rule.NextAttemptAt = DateTime.UtcNow; rule.RunNowRevisionId = (await DesiredRevisionAsync(session, page, rule.PublishedOnly, ct))?.Id; rule.LastError = "";
        await jobs.RequestAsync(PagePdfExportJob.JobId, "Exportovat teď", ct: ct);
        return await ViewAsync(page, rule, ct);
    }
    public async Task<string> Handle(AuthorizePagePdfExportFolder command, CancellationToken ct)
    {
        var (page, _) = await RequirePageAsync(command.PageId, ct);
        await profiles.RequireAvailableAsync(session, command.StorageProfileId, page.SpaceId, ct);
        return StorageProfilePaths.NormalizeFolder(command.Folder);
    }
    public async Task<PagePdfExportTarget?> Handle(FindUnmanagedPagePdfExportTarget query, CancellationToken ct)
    {
        var (page, _) = await RequirePageAsync(query.PageId, ct);
        if (!query.Request.Enabled) return null;
        var rule = await session.GetAsync<PagePdfExport>(page.Id, ct);
        var (profileId, folder, name) = await TargetAsync(page, rule, query.Request, ct);
        bool Same(Guid? profile, string? savedFolder, string? savedName) => profile == profileId
            && string.Equals(savedFolder, folder, StringComparison.OrdinalIgnoreCase) && string.Equals(savedName, name, StringComparison.OrdinalIgnoreCase);
        // The saved target was confirmed when it was saved and the last written one is this export's own file. A name another
        // page's export claims is refused by the save itself.
        if (rule is not null && (Same(rule.StorageProfileId, rule.Folder, rule.FileName) || Same(rule.LastStorageProfileId, rule.LastFolder, rule.LastFileName))
            || await ClaimedByOtherAsync(session, page.Id, profileId, folder, name, ct)) return null;
        return new(profileId, folder, name);
    }
    public async Task<IReadOnlyList<PagePdfExportOverviewRow>> Handle(ListPagePdfExports query, CancellationToken ct)
    {
        await administrators.RequireWikiAdminAsync(ct);
        var rules = await session.Query<PagePdfExport>().Take(1000).ToListAsync(ct);
        var pageIds = rules.Select(x => x.PageId).ToList();
        var pages = (await session.Query<Page>().Where(x => pageIds.Contains(x.Id) && !x.IsTrashed).ToListAsync(ct)).ToDictionary(x => x.Id);
        var current = (await session.Query<PageRevision>().Where(x => pageIds.Contains(x.PageId) && x.ActiveTo == null)
            .Select(x => new { x.PageId, x.Id, x.Title }).ToListAsync(ct)).ToDictionary(x => x.PageId);
        var spaceIds = pages.Values.Select(x => x.SpaceId).Distinct().ToList();
        var spaces = (await session.Query<Space>().Where(x => spaceIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var storage = (await session.Query<StorageProfile>().ToListAsync(ct)).ToDictionary(x => x.Id);
        var grants = (await session.Query<StorageProfileSpace>().ToListAsync(ct)).ToHashSet();
        return rules.Where(x => pages.ContainsKey(x.PageId)).Select(rule =>
        {
            var page = pages[rule.PageId];
            var profile = storage.GetValueOrDefault(rule.StorageProfileId);
            var available = profile is { Enabled: true, AllowPageExports: true }
                && (profile.AllSpaces || grants.Contains(new() { StorageProfileId = profile.Id, SpaceId = page.SpaceId }));
            var revision = current.GetValueOrDefault(page.Id);
            var path = profile is null ? rule.Folder + "/" + rule.FileName : StorageProfilePaths.Display(profile.Destination, rule.Folder, rule.FileName);
            return new PagePdfExportOverviewRow(page.Id, page.SpaceId, spaces.GetValueOrDefault(page.SpaceId) ?? "", revision?.Title ?? "",
                profile?.Name ?? "Odebrané úložiště", path, rule.Enabled, rule.PublishedOnly, rule.KeepVersions,
                StatusOf(rule, available, profile?.Version, rule.PublishedOnly ? page.PublishedRevisionId : revision?.Id), rule.LastExportedAt, rule.LastError);
        }).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task Handle(RetryPagePdfExport command, CancellationToken ct)
    {
        await administrators.RequireWikiAdminAsync(ct);
        var rule = await session.GetAsync<PagePdfExport>(command.PageId, LockMode.Upgrade, ct) ?? throw new ApiException(404, "Export nebyl nalezen.");
        var page = await session.GetAsync<Page>(rule.PageId, ct);
        if (!rule.Enabled || page is null || page.IsTrashed) throw new ApiException(400, "Automatický export je vypnutý.");
        // Like "Exportovat teď", but the export keeps running under the account that set it up.
        rule.Version++; rule.NextAttemptAt = DateTime.UtcNow; rule.RunNowRevisionId = (await DesiredRevisionAsync(session, page, rule.PublishedOnly, ct))?.Id;
        rule.LastError = ""; rule.FailureCount = 0;
        await jobs.RequestAsync(PagePdfExportJob.JobId, "Zkusit znovu", ct: ct);
    }
    /// <summary>The validated profile, folder (empty: the profile root) and PDF name of a save.</summary>
    private async Task<(Guid, string, string)> TargetAsync(Page page, PagePdfExport? rule, PagePdfExportRequest request, CancellationToken ct)
    {
        var profileId = request.StorageProfileId ?? rule?.StorageProfileId ?? throw new ApiException(400, "Vyberte úložiště.");
        if (request.Enabled || rule is null || profileId != rule.StorageProfileId || request.Folder != rule.Folder || request.FileName != rule.FileName)
            await profiles.RequireAvailableAsync(session, profileId, page.SpaceId, ct);
        var folder = StorageProfilePaths.NormalizeFolder(request.Folder);
        var name = request.FileName?.Trim() ?? throw new ApiException(400, "Chybí název PDF.");
        try { FileStorages.ValidateName(name); }
        catch (ArgumentException) { throw new ApiException(400, "Zadejte platný název PDF bez cesty."); }
        if (name.Length > 120) throw new ApiException(400, "Název PDF smí mít nejvýše 120 znaků.");
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ApiException(400, "Název souboru musí končit .pdf.");
        return (profileId, folder, name);
    }
    private async Task<(Page, UserAccount)> RequirePageAsync(Guid id, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var spaceId = await session.QueryOver<Page>().Where(x => x.Id == id).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty) throw new ApiException(404, "Stránka nebyla nalezena.");
        var actor = await access.LockActorAsync(spaceId, ct);
        var page = await session.GetAsync<Page>(id, ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        if (page.SpaceId != spaceId) throw new ApiException(409, "Stránka byla přesunuta. Obnovte nastavení.");
        if (page.IsTrashed) throw new ApiException(404, "Stránka nebyla nalezena.");
        await access.RequirePageAsync(page, AccessOperation.EditContent, ct);
        return (page, actor);
    }
    private async Task<PagePdfExportView> ViewAsync(Page page, PagePdfExport? rule, CancellationToken ct)
    {
        var available = await profiles.ListAvailableAsync(page.SpaceId, ct);
        if (rule is null)
        {
            // A new export is named after the page and proposes the profile root; the dialog browses for another folder.
            var title = (await DesiredRevisionAsync(session, page, false, ct))?.Title;
            return new(new(false, null, "", ContentExportNames.Stem(title, "document") + ".pdf", false, false, 0, PdfCoverDetails.All), available, "paused", null, null, "");
        }
        var revision = await DesiredRevisionAsync(session, page, rule.PublishedOnly, ct);
        var profile = await session.GetAsync<StorageProfile>(rule.StorageProfileId, ct);
        var status = StatusOf(rule, available.Any(x => x.Id == rule.StorageProfileId), profile?.Version, revision?.Id);
        return new(new(rule.Enabled, rule.StorageProfileId, rule.Folder, rule.FileName, rule.KeepVersions, rule.PublishedOnly, rule.Version, PdfCoverDetails.Parse(rule.CoverDetails)), available, status, rule.LastExportedAt, rule.LastRevisionId, rule.LastError, rule.LastFileName);
    }
    private static string StatusOf(PagePdfExport rule, bool available, int? profileVersion, Guid? desiredRevisionId)
        => !rule.Enabled || !available ? "paused"
            : profileVersion == rule.LastProfileVersion && desiredRevisionId is not null && rule.LastRevisionId == desiredRevisionId && rule.LastConfigurationVersion == rule.Version ? "current"
            : rule.LastError.Length > 0 ? "error" : "pending";
    /// <summary>Whether another page's export writes, or last wrote, this file. Case-insensitive, as SMB and Windows folders are. The file
    /// last written is dated ("a 2026-11-08 14-30.pdf"), so it is also the file of the name it was written for ("a.pdf").</summary>
    internal static async Task<bool> ClaimedByOtherAsync(ISession session, Guid pageId, Guid profileId, string folder, string name, CancellationToken ct)
    {
        folder = folder.ToLowerInvariant();
        var lower = name.ToLowerInvariant();
        if (await session.Query<PagePdfExport>().AnyAsync(x => x.PageId != pageId
            && x.StorageProfileId == profileId && x.Folder.ToLower() == folder && x.FileName.ToLower() == lower, ct)) return true;
        var written = await session.Query<PagePdfExport>()
            .Where(x => x.PageId != pageId && x.LastStorageProfileId == profileId && x.LastFolder!.ToLower() == folder)
            .Select(x => x.LastFileName!).ToListAsync(ct);
        return written.Any(file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PagePdfExportRunner.UndatedName(file), name, StringComparison.OrdinalIgnoreCase));
    }
    internal static async Task<PageRevision?> DesiredRevisionAsync(ISession session, Page page, bool published, CancellationToken ct)
        => published ? page.PublishedRevisionId is { } id ? await session.GetAsync<PageRevision>(id, ct) : null
            : await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
}
