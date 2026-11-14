using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Contracts;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Wolverine;

namespace Atlas.Server.Features.PageHistory;

public static class PageHistoryEndpoints
{
    public static void MapPageHistory(this IEndpointRouteBuilder app)
    {
        var pages = app.MapGroup("/api/pages/{id:guid}").RequireAuthorization();
        pages.MapGet("/history", async (Guid id, int? beforeVersion, int? limit, Guid? group, HttpContext context, IMessageBus bus, CancellationToken ct)
                => Private(context, await bus.InvokeAsync<PageHistoryList>(new GetPageHistory(id, beforeVersion, limit ?? 20, group), ct)))
            .Produces<PageHistoryList>()
            .ProducesProblem(400)
            .ProducesProblem(404);
        pages.MapGet("/history/{revisionId:guid}", async (Guid id, Guid revisionId, HttpContext context, IMessageBus bus, CancellationToken ct)
                => Private(context, await bus.InvokeAsync<PageHistoryRevision>(new GetPageRevision(id, revisionId), ct)))
            .Produces<PageHistoryRevision>()
            .ProducesProblem(404);
        pages.MapPost("/restore", async (Guid id, RestorePageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageDto>(new RestorePage(id, request), ct)))
            .Produces<PageDto>()
            .ProducesProblem(409);
        pages.MapPost("/versions", async (Guid id, NameVersionRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<NamedVersion>(new NameVersion(id, request.ExpectedVersion, request.Label), ct)))
            .Produces<NamedVersion>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }

    /// <summary>
    /// 3.6-D: an answer the browser may keep privately and revalidate (304) instead of downloading again. The history is filtered for its
    /// reader (hidden secret references), so the tag covers the reader, their security stamp (the epoch a revocation rotates) and the
    /// filtered body itself; it is never a shared cache and is answered only after the request was authorized like any other.
    /// </summary>
    internal static IResult Private<T>(HttpContext context, T value)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var body = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var claimed = context.RequestServices.GetRequiredService<ICurrentActor>().Claimed();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{claimed.Id:N}\n{claimed.SecurityStamp}\n"));
        hash.AppendData(body);
        var tag = new EntityTagHeaderValue('"' + Convert.ToHexStringLower(hash.GetHashAndReset()) + '"');
        context.Response.Headers.ETag = tag.ToString();
        context.Response.Headers.CacheControl = "private, no-cache";
        return context.Request.GetTypedHeaders().IfNoneMatch.Any(match => match.Compare(tag, useStrongComparison: false))
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(body, "application/json; charset=utf-8");
    }
}
