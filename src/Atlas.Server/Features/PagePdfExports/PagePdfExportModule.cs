using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Wolverine;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.PagePdfExports.Commands;
using Atlas.Server.Features.PagePdfExports.Queries;
namespace Atlas.Server.Features.PagePdfExports;

public static class PagePdfExportModule
{
    public static IServiceCollection AddPagePdfExports(this IServiceCollection services, bool scheduler = true)
    {
        services.AddScoped<BuildContentExportHandler>();
        services.AddSingleton<IContentPdfRenderer>(p => p.GetRequiredService<ContentPdfRenderer>());
        services.AddSingleton<PagePdfExportRunner>();
        if (scheduler) services.AddJob<PagePdfExportJob>();
        return services;
    }
    public static void MapPagePdfExports(this WebApplication app)
    {
        var group = app.MapGroup("/api/pages/{id:guid}/pdf-export").RequireAuthorization();
        group.MapGet("", (Guid id, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<PagePdfExportView>(new GetPagePdfExport(id), ct));
        group.MapPut("", async (Guid id, PagePdfExportRequest request, IMessageBus bus, StorageProfileService profiles,
            OidcRuntimeConfiguration oidc, CancellationToken ct) =>
        {
            // Storage is checked outside the save's transaction. shortcut: a file that appears between this check and the first
            // export is replaced without asking; a per-export record of confirmed files would close that window.
            if (!request.ConfirmTakeover && await bus.InvokeAsync<PagePdfExportTarget?>(new FindUnmanagedPagePdfExportTarget(id, request), ct) is { } target
                && await StorageAsync(async () =>
                {
                    try { using var storage = await profiles.OpenExistingAsync(target.StorageProfileId, target.Folder, ct); return await storage.ExistsAsync(target.FileName, ct); }
                    catch (DirectoryNotFoundException) { return false; }
                }))
                throw new ApiException(409, $"Ve složce už je soubor {target.FileName}, který tento export nevytvořil. Potvrďte, že ho má export převzít a přepisovat.", TakeoverCode);
            // The Host header is the client's word: a recurring export keeps only the configured public origin, else none, and its
            // PDF then keeps Atlas links relative (SEC-I-3).
            return await bus.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(id, request, oidc.PublicOrigin is { } origin ? origin + "/" : ""), ct);
        });
        group.MapPost("/run", (Guid id, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<PagePdfExportView>(new RunPagePdfExport(id), ct));
        group.MapGet("/folders", async (Guid id, Guid profileId, string? folder, IMessageBus bus, StorageProfileService profiles, CancellationToken ct) =>
        {
            var path = await bus.InvokeAsync<string>(new AuthorizePagePdfExportFolder(id, profileId, folder ?? ""), ct);
            return await StorageAsync(async () =>
            {
                using var storage = await profiles.OpenExistingAsync(profileId, path, ct);
                var listing = await storage.ListFoldersAsync(500, ct);
                return new PagePdfExportFolders(path, listing.Files.Select(x => x.Name).ToArray(), listing.Truncated);
            });
        });
        group.MapPost("/folders", async (Guid id, PagePdfExportFolderRequest request, IMessageBus bus, StorageProfileService profiles, CancellationToken ct) =>
        {
            var parent = await bus.InvokeAsync<string>(new AuthorizePagePdfExportFolder(id, request.StorageProfileId, request.Parent ?? ""), ct);
            var folder = await StorageAsync(() => profiles.CreateFolderAsync(request.StorageProfileId, parent, request.Name ?? "", ct));
            return new PagePdfExportFolders(folder, [], false);
        });
        var overview = app.MapGroup("/api/storage-profiles/pdf-exports").RequireAuthorization();
        overview.MapGet("", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<PagePdfExportOverviewRow>>(new ListPagePdfExports(), ct));
        overview.MapPost("/{pageId:guid}/run", async (Guid pageId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RetryPagePdfExport(pageId), ct);
            return Results.NoContent();
        });
    }
    public const string TakeoverCode = "pdf_target_exists";
    /// <summary>Storage failures as answers people can act on, without provider details that may name the service account.</summary>
    private static async Task<T> StorageAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (DirectoryNotFoundException) { throw new ApiException(404, "Složka v úložišti neexistuje. Obnovte výpis složek."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ApiException(503, "Úložiště není dostupné nebo k této složce nemá server oprávnění. Zkuste to znovu."); }
    }
}
/// <summary>
/// The automatic PDF export (3.3 plan §1 row 21): passes of <see cref="PagePdfExportRunner.RunDueAsync"/> while a pass is full, at the
/// time the exports' own data says (<see cref="PagePdfExportRunner.NextDueAsync"/>). A page's save asks again (PagePdfExportRevision),
/// a saved rule, "Exportovat teď" and "Zkusit znovu" request a run. The failures, backoff, Czech LastError and the per-page lock stay
/// in the PagePdfExport rows and the runner.
/// </summary>
public sealed class PagePdfExportJob(PagePdfExportRunner runner) : IJobDefinition
{
    public const string JobId = "pdf-exports.export";

    /// <summary>A saved revision is exported once it has been left alone this long.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    /// <summary>Between full passes, and the soonest next run: an export the renderer or another export of the page held stays due.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    public string Id => JobId;
    public string Title => "Export stránek do PDF";
    public string Description => "Uloží PDF stránek s automatickým exportem do jejich úložiště, když se stránka po uložení 30 s nemění.";
    public JobSchedule? Schedule => JobSchedule.FromData;

    public async Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct)
    {
        var soonest = DateTime.UtcNow + Pause;
        var due = await runner.NextDueAsync(ct);
        return due < soonest ? soonest : due;
    }

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var total = 0;
        int due;
        while ((due = await runner.RunDueAsync(DateTime.UtcNow, ct)) > 0)
        {
            total += due;
            context.Summarize(new { pages = total });
            if (due < PagePdfExportRunner.Batch) break;
            await Task.Delay(Pause, ct);
        }
        context.Log.LogInformation("Zpracováno {Count} exportů", total);
    }
}

/// <summary>A page's new working or published revision: its export, if it has one, is due once the revision settles. A publication
/// followed by a draft still reaches a published-only export.</summary>
public sealed record PagePdfExportRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == "atlas.page";
}

public static class PagePdfExportRevisionHandler
{
    public static async Task Handle(PagePdfExportRevision delivery, ISession session, IJobRequests requests, CancellationToken ct)
    {
        // Most pages have no export: their saves ask nothing.
        if (await session.GetAsync<PagePdfExport>(delivery.Event.ResourceId, ct) is { Enabled: true } rule
            && (!rule.PublishedOnly || delivery.Event.RevisionKind != "working"))
        {
            await requests.RescheduleAsync(PagePdfExportJob.JobId, ct);
        }
    }
}
