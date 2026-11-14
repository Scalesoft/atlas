using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Wolverine;

namespace Atlas.Server.Features.AccessAudit;

public static class AccessAuditEndpoints
{
    public static void MapAccessAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/access-audit", async (string? beforeId, int? limit, Guid? vaultId, Guid? userId, Guid? groupId, Guid? spaceId,
                IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessAuditList>(new GetAccessAudit(beforeId, limit ?? 50, vaultId, userId, groupId, spaceId), ct)))
            .RequireAuthorization()
            .WithName("ListAccessAudit")
            .Produces<AccessAuditList>();
        app.MapGet("/api/admin/activity-summary", async (int? days, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ActivitySummary>(new GetActivitySummary(days), ct)))
            .RequireAuthorization()
            .WithName("GetActivitySummary")
            .Produces<ActivitySummary>();
    }
}
