using Microsoft.Net.Http.Headers;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.ContentExport;

public static class ContentExportEndpoints
{
    public static void MapContentExport(this WebApplication app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy), true);
    }

    private static void Map(RouteGroupBuilder group, bool personal)
    {
        foreach (var wholeSpace in new[] { false, true })
        {
            var space = wholeSpace;
            var endpoint = group.MapGet($"/{(space ? "spaces" : "pages")}/{{id:guid}}/export",
                async (Guid id, string? format, Guid? revisionId, string? details, HttpContext context, IMessageBus bus, ContentPdfRenderer pdf, TimeProvider clock,
                    OidcRuntimeConfiguration oidc, CancellationToken ct) =>
                {
                    if (format is not (null or "zip" or "pdf") || space && format == "pdf")
                    {
                        throw new ApiException(400, "PDF lze exportovat pouze pro jednotlivou stránku. Podporované formáty jsou zip a pdf.");
                    }
                    // What the PDF cover states about the revision: comma-separated names, empty for none, absent for all.
                    var coverDetails = PdfCoverDetails.Parse(details);
                    var archive = await bus.InvokeAsync<ContentExportArchive>(new BuildContentExport(id, space, revisionId), ct);
                    RevalidateContentExport revalidate;
                    DownloadLease lease;
                    long proven;
                    try
                    {
                        if (format == "pdf")
                        {
                            var source = archive;
                            var origin = oidc.PublicOrigin ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
                            try { archive = await pdf.RenderAsync(source, origin + "/", coverDetails, ct); }
                            catch (ApiException busy) when (busy.Code == ContentPdfRenderer.BusyCode)
                            {
                                context.Response.Headers.RetryAfter = (string)busy.Extensions!["retryAfter"]!;
                                throw;
                            }
                            finally { await source.DisposeAsync(); }
                        }
                        Guid? tokenId = null;
                        if (personal)
                        {
                            if (!Guid.TryParse(context.User.FindFirst("api_token_id")?.Value, out var authenticatedToken))
                            {
                                throw new ApiException(401, "Platnost osobního API tokenu během exportu skončila.");
                            }
                            tokenId = authenticatedToken;
                        }
                        revalidate = new RevalidateContentExport(archive.SpaceId, archive.Selected, tokenId, space);
                        lease = await bus.InvokeAsync<DownloadLease>(revalidate, ct);
                        // The moment the final authorization was proven, on the monotonic clock: the download renews it from here and does not repeat it at its start.
                        proven = clock.GetTimestamp();
                    }
                    catch
                    {
                        await archive.DisposeAsync();
                        throw;
                    }
                    context.Response.Headers.CacheControl = "private, no-store";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                    return RevalidatingDownload.Proven(Results.File(archive.Stream, format == "pdf" ? "application/pdf" : "application/zip", archive.Name, entityTag: new EntityTagHeaderValue('"' + archive.Sha256 + '"')), archive.Stream,
                        lease, proven, token => bus.InvokeAsync<DownloadLease>(revalidate, token), answer => answer.Until, clock);
                }).RequireRateLimiting("content-export").Produces<Stream>(200, "application/zip", space ? [] : ["application/pdf"]).ProducesProblem(400).ProducesProblem(422).ProducesProblem(503).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
            if (personal)
            {
                endpoint.WithName(space ? "ExportSpaceContent" : "ExportPageContent");
            }
        }
    }
}
