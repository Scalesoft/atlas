using Atlas.Server.Features.Pages.Contracts;
using Wolverine;

namespace Atlas.Server.Features.PageTemplates;

public sealed record SetPageTemplate(Guid PageId, bool IsTemplate);
public sealed record SetPageTemplateRequest(bool IsTemplate);
public sealed record ListPageTemplates(Guid SpaceId);
public sealed record PageTemplateSummary(Guid Id, Guid SpaceId, Guid? ParentId, string Title, int Version, bool IsPublished);

public static class PageTemplatesEndpoints
{
    public static void MapPageTemplates(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/spaces/{spaceId:guid}/page-templates", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IReadOnlyList<PageTemplateSummary>>(new ListPageTemplates(spaceId), ct)))
            .RequireAuthorization();
        app.MapPut("/api/pages/{id:guid}/template", async (Guid id, SetPageTemplateRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(PageView.From(await bus.InvokeAsync<PageDto>(new SetPageTemplate(id, request.IsTemplate), ct))))
            .RequireAuthorization();
    }
}
