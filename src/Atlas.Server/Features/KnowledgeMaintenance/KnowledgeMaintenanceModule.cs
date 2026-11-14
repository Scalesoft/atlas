using Atlas.Server.Features.KnowledgeMaintenance.Commands;
using Atlas.Server.Features.KnowledgeMaintenance.Queries;
using Wolverine;

namespace Atlas.Server.Features.KnowledgeMaintenance;

public static class KnowledgeMaintenanceModule
{
    public static IServiceCollection AddKnowledgeMaintenance(this IServiceCollection services)
        => services.AddScoped<DuplicatePageStore>().AddScoped<AiEvaluationRunner>().AddScoped<ContentValidationStore>();

    public static void MapKnowledgeMaintenance(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/interactions", async (RecordAiInteraction request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<bool>(request, ct);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("interactive-search");
        var reports = app.MapGroup("/api/admin/ai").RequireAuthorization();
        reports.MapPost("/evaluate", async (AiEvaluationRunner runner, CancellationToken ct) => Results.Ok(await runner.RunAsync(ct))).RequireRateLimiting("ai-admin-test");
        reports.MapGet("/usage", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiUsageReport>(new GetAiUsageReport(), ct)));
        reports.MapGet("/quality", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiQualityReport>(new GetAiQualityReport(), ct)));
        reports.MapGet("/usage.csv", async (IMessageBus bus, CancellationToken ct)
            => Results.File(System.Text.Encoding.UTF8.GetBytes(AiReportCsv.Write(await bus.InvokeAsync<AiUsageReport>(new GetAiUsageReport(), ct))),
                "text/csv; charset=utf-8", "atlas-ai-usage.csv"));
        var group = app.MapGroup("/api/spaces/{spaceId:guid}/duplicates").RequireAuthorization();
        group.MapGet("", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<DuplicatePageList>(new GetDuplicatePages(spaceId), ct)));
        group.MapPost("/dismiss", async (Guid spaceId, DismissDuplicateRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<bool>(new DismissDuplicatePages(spaceId, request.PageId, request.OtherPageId), ct);
            return Results.NoContent();
        });
        // Space administrators: stored revisions the pinned editor would refuse to open (ContentValidation.cs). Read-only.
        app.MapGet("/api/spaces/{spaceId:guid}/content-validation", async (Guid spaceId, Guid? cursor, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ContentValidationReport>(new GetContentValidation(spaceId, cursor), ct))).RequireAuthorization();
    }
}

public sealed record DismissDuplicateRequest(Guid PageId, Guid OtherPageId);
