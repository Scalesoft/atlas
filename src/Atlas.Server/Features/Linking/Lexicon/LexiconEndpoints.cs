using Atlas.Server.Features.Linking.Lexicon.Queries;
using Microsoft.Net.Http.Headers;
using Wolverine;

namespace Atlas.Server.Features.Linking.Lexicon;

/// <summary>The place lexicon of the linking engine (audit engine D5). The client keeps the last answer and revalidates it by ETag.</summary>
public static class LexiconEndpoints
{
    public static void MapLexicon(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/spaces/{spaceId:guid}/lexicon", async (Guid spaceId, HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            var lexicon = await bus.InvokeAsync<SpaceLexicon>(new GetSpaceLexicon(spaceId), ct);
            var tag = new EntityTagHeaderValue('"' + lexicon.Version + '"');
            context.Response.Headers.ETag = tag.ToString();
            context.Response.Headers.CacheControl = "private, no-cache";
            return context.Request.GetTypedHeaders().IfNoneMatch.Any(match => match.Compare(tag, useStrongComparison: false))
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Ok(lexicon);
        }).RequireAuthorization();
    }
}
