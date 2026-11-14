using System.IO.Compression;
using System.Security.Cryptography;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Microsoft.Net.Http.Headers;
using Wolverine;

namespace Atlas.Server.Features.Pages;

public static class PagesEndpoints
{
    public static void MapPages(this IEndpointRouteBuilder app)
    {
        var drafts = app.MapGroup("/api/spaces/{spaceId:guid}/drafts").RequireAuthorization();
        drafts.MapGet("", async (Guid spaceId, string? q, Guid? authorId, string? kind, int? offset, int? limit, Guid? pageId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceDraftList>(new GetSpaceDrafts(spaceId, q, authorId, kind ?? "all", offset ?? 0, limit ?? 100, pageId), ct)))
            .Produces<SpaceDraftList>()
            .ProducesProblem(400)
            .ProducesProblem(403);
        drafts.MapPost("/{id:guid}/approve", async (Guid spaceId, Guid id, ApproveSpaceDraftRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageDto>(new PublishPage(id, new PublishPageRequest(request.ExpectedVersion, request.ChangeSummary),
                    new SpaceDraftReview(spaceId, request.ExpectedRevisionId)), ct)))
            .Produces<PageDto>()
            .ProducesProblem(403)
            .ProducesProblem(409);
        var pages = app.MapGroup("/api/pages").RequireAuthorization();
        pages.MapGet("/{id:guid}", async (Guid id, bool? bundle, bool? visit, AiCapabilities capabilities, IMessageBus bus, LiveSeeds seeds, CancellationToken ct) =>
            {
                var view = bundle == true
                    ? await bus.InvokeAsync<PageView>(new GetPageWithBundle(id, await GetPageConnections.ForReaderAsync(id, 10, capabilities, ct), visit != true), ct)
                    : PageView.From(await bus.InvokeAsync<PageDto>(new GetPage(id), ct));
                // 3.6: a page whose live state the server has not seeded yet (its announcement is still on the way, the backfill has not
                // reached it) is seeded now, outside the read's transaction, for a bounded while; then only the live state is read again.
                if (view.Bundle is { Collaboration: null } unseeded && (view.CanEdit || view.Live) && await seeds.EnsureWithinAsync(id, ct))
                {
                    view = view with { Bundle = unseeded with
                    {
                        Collaboration = (await bus.InvokeAsync<PageBundleCollaborationRead>(new GetPageBundleCollaboration(id), ct)).Collaboration
                    } };
                }
                // `visit`: the app opens the page, so this read records the visit (3.6-G N7: no request of its own); with the bundle its
                // answer is the personal state. A read ahead on hover asks without it.
                if (visit != true) return Results.Ok(view);
                var visited = await bus.InvokeAsync<PersonalPageState>(new RecordPageVisit(id), ct);
                return Results.Ok(view.Bundle is { } parts ? view with { Bundle = parts with { PersonalState = visited } } : view);
            })
            .Produces<PageView>()
            .ProducesProblem(404);
        // 3.6: the live Yjs state of the session the bundle names, read beside the bundle. The reader's browser keeps it (private) and asks
        // again on every open (no-cache): each request is authorized as the bundle is, and an unchanged state answers 304, so a warm open
        // does not download it again. The tag names the generation, which the client checks against its bundle's; 204: none to build from.
        pages.MapGet("/{id:guid}/collaboration/state", async (Guid id, HttpContext context, IMessageBus bus, CancellationToken ct) =>
            {
                if ((await bus.InvokeAsync<PageLiveStateRead>(new GetPageLiveState(id), ct)).Live is not { } live)
                {
                    return Results.NoContent();
                }
                var tag = new EntityTagHeaderValue($"\"{live.Generation}.{Convert.ToHexStringLower(SHA256.HashData(live.State))}\"");
                context.Response.Headers.ETag = tag.ToString();
                context.Response.Headers.CacheControl = "private, no-cache";
                if (context.Request.GetTypedHeaders().IfNoneMatch.Any(match => match.Compare(tag, useStrongComparison: false)))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                var gzipped = new MemoryStream();
                using (var gzip = new GZipStream(gzipped, CompressionLevel.Optimal))
                {
                    gzip.Write(live.State);
                }
                return Results.Bytes(gzipped.ToArray(), "application/octet-stream");
            })
            .Produces(200, contentType: "application/octet-stream")
            .Produces(204)
            .Produces(304);
        pages.MapPost("", async (CreatePageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(PageView.From(await bus.InvokeAsync<PageDto>(new CreatePage(request), ct))))
            .Produces<PageView>()
            .ProducesProblem(400);
        pages.MapPut("/{id:guid}", async (Guid id, SavePageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(PageView.From(await bus.InvokeAsync<PageDto>(new SavePage(id, request), ct))))
            .Produces<PageView>()
            .ProducesProblem(409);
        pages.MapPost("/{id:guid}/publish", async (Guid id, PublishPageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(PageView.From(await bus.InvokeAsync<PageDto>(new PublishPage(id, request), ct))))
            .Produces<PageView>()
            .ProducesProblem(409);
    }
}
