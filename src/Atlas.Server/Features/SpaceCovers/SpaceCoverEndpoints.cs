using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.SpaceCovers.Commands;
using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Features.SpaceCovers.Queries;
using Atlas.Server.Features.SpaceSettings.Domain;
using Wolverine;

namespace Atlas.Server.Features.SpaceCovers;

/// <summary>The card image of a space (spaceCover.ts). Cookie API only, like the space settings; CSRF comes from the pipeline.</summary>
public static class SpaceCoverEndpoints
{
    /// <summary>The image plus the multipart envelope.</summary>
    public const long MaximumRequestBytes = SpaceCoverImage.MaximumBytes + 1024 * 1024;

    public static void MapSpaceCovers(this IEndpointRouteBuilder app)
    {
        var cover = app.MapGroup("/api/spaces/{id:guid}/cover").RequireAuthorization();
        cover.MapGet("", async (Guid id, string? v, HttpContext context, IMessageBus bus, AttachmentFiles files, CancellationToken ct) =>
        {
            var content = await bus.InvokeAsync<SpaceCoverContent>(new OpenSpaceCover(id), ct);
            Stream stream;
            try
            {
                stream = files.Open(content.FileId);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ApiException(404, "Prostor nemá úvodní obrázek.");
            }
            // The URL names the version, so the current one can stay in the browser for good. A stale ?v= (a list loaded before
            // a change) still gets the current image, but must not pin it under the old address.
            context.Response.Headers.CacheControl = v == content.Version ? "private, max-age=31536000, immutable" : "private, no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.File(stream, content.MimeType, entityTag: new EntityTagHeaderValue('"' + content.Sha256 + '"'));
        }).Produces<Stream>(200, "image/png", "image/jpeg", "image/webp")
          .ProducesProblem(404);

        cover.MapPut("", async (Guid id, HttpContext context, IMessageBus bus, ICurrentActor currentActor, AttachmentFiles files,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            // As for attachments: 401 first, then the multipart shape, then the space rule in the handler.
            await currentActor.RequireAsync(ct);
            if (!context.Request.HasFormContentType)
            {
                throw new ApiException(415, "Nahrání vyžaduje multipart/form-data.");
            }
            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
            {
                throw new ApiException(400, "Vyberte právě jeden soubor v poli file.");
            }
            SpaceCoverImage.RequireSize(file.Length);
            var content = new byte[(int)file.Length];
            await using (var input = file.OpenReadStream())
            {
                await input.ReadExactlyAsync(content, ct);
            }
            var change = await bus.InvokeAsync<SpaceCoverChange>(new ReplaceSpaceCover(id, content), ct);
            Release(files, change, loggers);
            return Results.Ok(change.Cover);
        }).WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumRequestBytes })
          .Accepts<SpaceCoverUploadForm>("multipart/form-data")
          .Produces<SpaceCoverDto>()
          .ProducesProblem(400)
          .ProducesProblem(404)
          .ProducesProblem(413);

        cover.MapPatch("", async (Guid id, SpaceCoverTintRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            var tinted = request.Tinted ?? throw new ApiException(400, "Zvolte, zda se má obrázek sladit do barev prostoru.");
            return Results.Ok((await bus.InvokeAsync<SpaceCoverChange>(new SetSpaceCoverTint(id, tinted), ct)).Cover);
        }).Produces<SpaceCoverDto>()
          .ProducesProblem(404);

        cover.MapDelete("", async (Guid id, IMessageBus bus, AttachmentFiles files, ILoggerFactory loggers, CancellationToken ct) =>
        {
            Release(files, await bus.InvokeAsync<SpaceCoverChange>(new RemoveSpaceCover(id), ct), loggers);
            return Results.NoContent();
        }).Produces(204)
          .ProducesProblem(404);

        cover.MapPut("/choice", async (Guid id, SpaceCoverChoiceRequest request, IMessageBus bus, AttachmentFiles files, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var change = await bus.InvokeAsync<SpaceCoverChange>(new ChooseSpaceCover(id, request.Source, request.CatalogKey), ct);
            Release(files, change, loggers);
            return Results.Ok(change.Cover);
        }).Produces<SpaceCoverDto>()
          .ProducesProblem(400)
          .ProducesProblem(404);

        // The automatic pick for a space still in a form. Pure: no handler, no session. POST keeps names out of URLs and logs;
        // 200 / 2000 / 40 are what a space allows (Rules.Required, SaveSpaceSettingsHandler, the icon column).
        app.MapPost("/api/space-covers/match", (SpaceCoverMatchRequest request) =>
        {
            if (request.Name is { Length: > 200 } || request.Description is { Length: > 2000 } || request.Icon is { Length: > 40 })
            {
                throw new ApiException(400, "Název smí mít nejvýše 200 znaků, popis 2000 a ikona 40.");
            }
            return Results.Ok(new SpaceCoverMatch(CoverCatalog.Match(request.Name ?? "", request.Description, request.Icon ?? SpaceAppearance.DefaultIcon)));
        }).RequireAuthorization()
          .RequireRateLimiting("interactive-search")
          .Produces<SpaceCoverMatch>()
          .ProducesProblem(400);
    }

    /// <summary>Deletes the file a committed change released. A failure leaves an unreferenced file behind, never a broken image.</summary>
    private static void Release(AttachmentFiles files, SpaceCoverChange change, ILoggerFactory loggers)
    {
        if (change.ReleasedFileId is not { } released)
        {
            return;
        }
        try
        {
            files.Remove(released);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            loggers.CreateLogger(typeof(SpaceCoverEndpoints)).LogWarning(e, "Could not delete the released space cover file {FileId}", released);
        }
    }
}
