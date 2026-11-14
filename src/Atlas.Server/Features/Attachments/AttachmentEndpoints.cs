using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Queries;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Attachments;

public static class AttachmentEndpoints
{
    public const long MaximumRequestBytes = AttachmentFiles.MaximumFileBytes + 1024 * 1024;

    public static void MapAttachments(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void Map(RouteGroupBuilder group, bool personal)
    {
        var upload = group.MapPost("/pages/{pageId:guid}/attachments", async (Guid pageId, HttpContext context, IMessageBus bus, ICurrentActor currentActor, AttachmentFiles files, CancellationToken ct) =>
        {
            // Legacy resolved the actor first (401 only) so the multipart shape was validated before authorization,
            // and only checked the page/space grant once it had a real file to store.
            await currentActor.RequireAsync(ct);
            // Unreachable over HTTP in both this endpoint and the legacy one: .Accepts<AttachmentUploadForm>("multipart/form-data")
            // below makes ASP.NET Core's routing (ConsumesMatcherPolicy) answer 404 for any other Content-Type before this
            // lambda ever runs. Left in for defense in depth / non-HTTP callers; do not rely on it to observe 415 over HTTP.
            if (!context.Request.HasFormContentType)
            {
                throw new ApiException(415, "Upload vyžaduje multipart/form-data.");
            }
            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
            {
                throw new ApiException(400, "Vyberte právě jeden soubor v poli file.");
            }
            var kind = form["kind"].ToString();
            if (kind is not ("" or "file" or "image"))
            {
                throw new ApiException(400, "Neplatný typ přílohy.");
            }
            var image = kind == "image";
            if (file.Length > (image ? AttachmentFiles.MaximumImageBytes : AttachmentFiles.MaximumFileBytes))
            {
                throw new ApiException(413, "Příloha překračuje povolenou velikost.");
            }
            await bus.InvokeAsync(new AuthorizeAttachmentUpload(pageId), ct);
            var id = Guid.NewGuid();
            await using var stream = file.OpenReadStream();
            var stored = await files.WriteAsync(id, stream, image, ct, file.FileName);
            var result = await bus.InvokeAsync<AttachmentDto>(new StoreAttachment(id, pageId, file.FileName, stored.Size, stored.Hash, stored.MimeType, image), ct);
            // Stored document URLs stay canonical; API consumers receive a bearer-capable download link separately.
            if (personal)
            {
                result = result with { DownloadUrl = result.DownloadUrl.Replace("/api/", "/api/v1/", StringComparison.Ordinal) };
            }
            return Results.Created(result.DownloadUrl, result);
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumRequestBytes })
          .Accepts<AttachmentUploadForm>("multipart/form-data")
          .Produces<AttachmentDto>(201).ProducesProblem(400).ProducesProblem(404).ProducesProblem(413).ProducesProblem(415);
        if (personal)
        {
            upload.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.AttachmentUpload).WithName("UploadPageAttachment");
        }
        // The list and the metadata decide exactly as the download does (AttachmentCatalog): an editor sees every active attachment of
        // the page, a reader only those the published revision references.
        var list = group.MapGet("/pages/{pageId:guid}/attachments", async (Guid pageId, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<PageAttachmentsDto>(new ListPageAttachments(pageId), ct);
            return Results.Ok(personal ? result with { Items = result.Items.Select(WithApiDownload).ToArray() } : result);
        }).Produces<PageAttachmentsDto>().ProducesProblem(404)
          .WithSummary("List the page's attachments the caller may open: editors get every active upload, readers only what the published revision references. usedInPublished/usedInWorking tell an upload from an attachment a revision uses.");
        var info = group.MapGet("/attachments/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<AttachmentInfoDto>(new GetAttachmentInfo(id), ct);
            return Results.Ok(personal ? WithApiDownload(result) : result);
        }).Produces<AttachmentInfoDto>().ProducesProblem(404)
          .WithSummary("Get one attachment's metadata (name, MIME type, size, SHA-256, links) under the same access as its download.");
        if (personal)
        {
            list.WithName("ListPageAttachments");
            info.WithName("GetAttachment");
        }
        foreach (var inline in new[] { false, true })
        {
            var content = inline;
            var endpoint = group.MapMethods($"/attachments/{{id:guid}}/{(content ? "content" : "download")}", ["GET", "HEAD"],
                async (Guid id, HttpContext context, IMessageBus bus, AttachmentFiles files, TimeProvider clock, CancellationToken ct) =>
                {
                    var open = new OpenAttachment(id, content, DownloadCredentials.TokenId(context, personal));
                    var attachment = await bus.InvokeAsync<AttachmentContentDto>(open, ct);
                    // The moment this authorization was proven, on the monotonic clock: the download renews it from here and does not repeat it at its start.
                    var proven = clock.GetTimestamp();
                    Stream stream;
                    try
                    {
                        stream = files.Open(id);
                    }
                    catch (FileNotFoundException)
                    {
                        throw new ApiException(404, "Příloha není dostupná.");
                    }
                    catch (DirectoryNotFoundException)
                    {
                        throw new ApiException(404, "Příloha není dostupná.");
                    }
                    // The image of an article may stay in the reader's own browser, which has to ask before every use (no-cache):
                    // each use is authorized above like a first request, and a matching ETag only saves the bytes (304). Access
                    // can be removed at any time, so there is no lifetime in which the copy is shown unasked. File downloads and
                    // the personal API stay out of caches.
                    context.Response.Headers.CacheControl = content && !personal ? "private, no-cache" : "private, no-store";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                    return RevalidatingDownload.Proven(Results.File(stream, content ? attachment.MimeType : "application/octet-stream", content ? null : attachment.Name,
                        lastModified: new DateTimeOffset(attachment.ActiveFrom), entityTag: new EntityTagHeaderValue('"' + attachment.Sha256 + '"'), enableRangeProcessing: true), stream,
                        attachment, proven, token => bus.InvokeAsync<AttachmentContentDto>(open, token), answer => answer.AuthorityUntil, clock);
                }).Produces<Stream>(200, content ? "image/png" : "application/octet-stream", content ? ["image/jpeg", "image/gif", "image/webp"] : [])
                  .Produces<Stream>(206, content ? "image/png" : "application/octet-stream", content ? ["image/jpeg", "image/gif", "image/webp"] : [])
                  .ProducesProblem(404);
            if (personal)
            {
                endpoint.WithName(content ? "GetAttachmentImage" : "DownloadAttachment");
            }
        }
    }

    /// <summary>The personal API's bearer-capable download next to the canonical links, which stay as stored in documents.</summary>
    private static AttachmentInfoDto WithApiDownload(AttachmentInfoDto attachment)
        => attachment with { ApiDownloadUrl = $"/api/v1/attachments/{attachment.Id:D}/download" };
}
