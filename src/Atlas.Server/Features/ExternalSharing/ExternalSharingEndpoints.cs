using Microsoft.Net.Http.Headers;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Queries;
using Wolverine;

namespace Atlas.Server.Features.ExternalSharing;

public static class ExternalSharingEndpoints
{
    public static void MapExternalSharing(this IEndpointRouteBuilder app)
    {
        var spaces = app.MapGroup("/api/spaces/{spaceId:guid}").RequireAuthorization();
        spaces.MapGet("/external-sharing", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SharingPolicyDto>(new GetSharingPolicy(spaceId), ct)));
        spaces.MapPut("/external-sharing", async (Guid spaceId, SaveSharingPolicyRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SharingPolicyDto>(new SaveSharingPolicy(spaceId, request), ct)));
        spaces.MapGet("/share-links", async (Guid spaceId, int? offset, int? limit, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ShareLinksDto>(new ListShareLinks(spaceId, null, offset ?? 0, limit ?? 50), ct)));
        var pages = app.MapGroup("/api/pages/{pageId:guid}/share-links").RequireAuthorization();
        pages.MapGet("", async (Guid pageId, int? offset, int? limit, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ShareLinksDto>(new ListShareLinks(Guid.Empty, pageId, offset ?? 0, limit ?? 50), ct)));
        pages.MapPost("", async (Guid pageId, CreateShareLinkRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            var issued = await bus.InvokeAsync<IssuedShareLink>(new CreateShareLink(pageId, request), ct);
            var result = new CreatedShareLinkDto(issued.Link, "/s/" + issued.Secret);
            // Location is management metadata, not a bearer secret (also avoids middleware logging it).
            return Results.Created($"/api/pages/{pageId}/share-links/{issued.Link.Id}", result);
        });
        pages.MapDelete("/{linkId:guid}", async (Guid pageId, Guid linkId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeShareLink(pageId, linkId), ct);
            return Results.NoContent();
        });
        var shared = app.MapGroup("/api/shared/{token}").AllowAnonymous().RequireRateLimiting("external-sharing");
        shared.MapGet("", async (string token, IMessageBus bus, CancellationToken ct)
            => Results.Ok(SharedDocument.ForToken(await bus.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(token)), ct), token)));
        foreach (var inline in new[] { false, true })
        {
            var content = inline;
            shared.MapMethods($"/attachments/{{id:guid}}/{(content ? "content" : "download")}", ["GET", "HEAD"],
                async (string token, Guid id, HttpContext context, IMessageBus bus, AttachmentFiles files, TimeProvider clock, CancellationToken ct) =>
                {
                    var open = new OpenSharedAttachment(ShareSecret.Hash(token), id, content);
                    var attachment = await bus.InvokeAsync<AttachmentContentDto>(open, ct);
                    // The moment this authorization was proven, on the monotonic clock: the download renews it from here and does not repeat it at its start.
                    var proven = clock.GetTimestamp();
                    Stream stream;
                    try { stream = files.Open(id); }
                    catch (FileNotFoundException) { throw ExternalSharingAccess.Missing(); }
                    catch (DirectoryNotFoundException) { throw ExternalSharingAccess.Missing(); }
                    context.Response.Headers.CacheControl = "private, no-store";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                    return RevalidatingDownload.Proven(Results.File(stream, content ? attachment.MimeType : "application/octet-stream", content ? null : attachment.Name,
                        lastModified: new DateTimeOffset(attachment.ActiveFrom), entityTag: new EntityTagHeaderValue('"' + attachment.Sha256 + '"'), enableRangeProcessing: true), stream,
                        attachment, proven, cancellation => bus.InvokeAsync<AttachmentContentDto>(open, cancellation), answer => answer.AuthorityUntil, clock);
                });
        }
    }
}
