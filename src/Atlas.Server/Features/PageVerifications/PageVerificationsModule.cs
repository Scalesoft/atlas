using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PageVerifications.Commands;
using Atlas.Server.Features.PageVerifications.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Jobs;
using FluentMigrator;
using Wolverine;

namespace Atlas.Server.Features.PageVerifications;

[Migration(2026112400, "5.2 Ověřený obsah: the space's verification interval and the page's steward and last verification")]
public sealed class PageVerificationMigration() : EmbeddedSchemaMigration("2026112400_page_verification.sql");

/// <summary>5.2 Ověřený obsah a čtenost (docs/atlas-5.2-plan.md): the web's routes and, for AI parity, the same under /api/v1 (reads with the
/// read scope; verifying, the steward, the interval and a report with the write scope).</summary>
public static class PageVerificationsModule
{
    public static IServiceCollection AddPageVerifications(this IServiceCollection services)
        // The MCP tools run the shared handlers behind G5 (PageVerificationMcpHandler).
        => services.AddScoped<PageVerificationReader>().AddScoped<Commands.PageVerificationHandler>().AddScoped<Queries.PageVerificationQueryHandler>()
            .AddScoped<Queries.SpaceReadershipHandler>()
            .AddJob<ContentVerificationJob>();

    public static void MapPageVerifications(this WebApplication app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        var read = group.MapGet("/pages/{id:guid}/verification", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageVerificationView>(new GetPageVerification(id), ct)))
            .Produces<PageVerificationView>().ProducesProblem(404)
            .WithSummary("The page's verification: state (off, unverified, verified, expiring = within 14 days, expired), the steward (garant), " +
                "who verified which published version when and until when, changedSinceVerification (a newer version was published since), " +
                "whether it was verified with AI, and the page's and the space's interval in months.");
        var set = group.MapPut("/pages/{id:guid}/verification", async (Guid id, SetPageVerificationRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageVerificationView>(new SetPageVerification(id, request), ct)))
            .Produces<PageVerificationView>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Verify the page's published version (verify: true, optionally until a later date), set its steward (stewardId: an active " +
                "user who may read the page, stewardFromLastAuthor: the author of the last published version, or clearSteward: true) or its interval (intervalMonths 0 = " +
                "not verified, 3, 6, 12; inheritInterval: true follows the space). Needs the right to edit the page; templates are not verified.");
        var report = group.MapPost("/pages/{id:guid}/verification/report", async (Guid id, ReportPageOutdatedRequest request, IMessageBus bus,
                CancellationToken ct) => Results.Ok(await bus.InvokeAsync<PageOutdatedReport>(new ReportPageOutdated(id, request), ct)))
            .Produces<PageOutdatedReport>().ProducesProblem(400).ProducesProblem(404)
            .WithSummary("Report the page as outdated with an optional note (at most 500 characters): its steward, else up to three editors who " +
                "last published it, get a notification. Any reader may; nothing on the page changes.");
        var health = group.MapGet("/spaces/{spaceId:guid}/content-health", async (Guid spaceId, string? state, bool? mine, string? sort, int? days,
                string? q, string? cursor, int? limit, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ContentHealthList>(new GetContentHealth(spaceId, state, mine ?? false, sort, days ?? 90, q, cursor,
                    limit ?? 50), ct)))
            .Produces<ContentHealthList>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Stav obsahu of a space for its admins and writers: counts by verification state and the published pages with their state, " +
                "steward, expiry, ancestors' titles and readership (views and distinct readers, no names); enabled: whether the space verifies. " +
                "state: all, due (expiring, expired, unverified), verified, expiring, lapsed (expired or unverified), no-steward, unread; mine: only where the caller is the steward; sort: priority (default: due pages first, then by readers), " +
                "expiry, readers, published; days: 30, 90, 365; q: part of the title; limit 1–100; cursor: nextCursor.");
        var batch = group.MapPost("/spaces/{spaceId:guid}/content-health/bulk", async (Guid spaceId, SetSpaceVerificationRequest request, IMessageBus bus,
                CancellationToken ct) => Results.Ok(await bus.InvokeAsync<SpaceVerificationResult>(new SetSpaceVerification(spaceId, request), ct)))
            .Produces<SpaceVerificationResult>().ProducesProblem(400).ProducesProblem(404)
            .WithSummary("Change up to 500 pages of the space at once: action verify, steward (stewardId, null clears it) or stewardFromLastAuthor " +
                "(the author of the last published version, only for pages without an active steward; the others are skipped as has_steward) over pageIds. Without pageIds, stewardFromLastAuthor sets the steward of every verified " +
                "page of the space that has none. Pages refused (no edit right, never published, …) are listed in skipped with the reason.");
        var readership = group.MapGet("/spaces/{spaceId:guid}/readership", async (Guid spaceId, DateOnly from, DateOnly to, string? filter, string? cursor,
                IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceReadership>(new GetSpaceReadership(spaceId, from, to, filter, cursor), ct)))
            .Produces<SpaceReadership>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Readership of the space's published pages over a period of up to 366 days for its admins and writers: views, distinct " +
                "readers, last view and last publication per page, most read first; counts only, never who. filter: all or unread.");
        if (!personalApi)
        {
            return;
        }
        read.WithName("GetPageVerification");
        health.WithName("GetContentHealth");
        readership.WithName("GetSpaceReadership");
        set.WithName("SetPageVerification").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageVerificationSet);
        report.WithName("ReportPageOutdated").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageReportOutdated);
        batch.WithName("SetSpaceVerification").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.SpaceVerificationBatch);
    }
}
