using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Atlas.Server.Features.ContentImport.Commands;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.ContentImport.Queries;
using Wolverine;

namespace Atlas.Server.Features.ContentImport;

public static class ContentImportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapContentImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/imports").RequireAuthorization();
        group.MapPost("/wiki-js/preview", async (HttpContext context, IMessageBus bus, ImportWorkGate gate, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireImportAdministrator(), ct);
            if (!context.Request.HasFormContentType)
            {
                throw new ApiException(415, "Nahrajte ZIP jako multipart formulář.");
            }
            var form = await context.Request.ReadFormAsync(ct);
            var upload = form.Files.GetFile("file") ?? throw new ApiException(400, "Chybí ZIP soubor.");
            if (form.Files.Count != 1 || upload.Length > WikiJsArchive.MaximumArchiveBytes)
            {
                throw new ApiException(413, "Nahrajte jeden ZIP o velikosti nejvýše 64 MiB.");
            }
            WikiImportOptions options;
            try
            {
                options = JsonSerializer.Deserialize<WikiImportOptions>(form["options"].ToString(), Json) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ApiException(400, "Neplatné nastavení importu.");
            }
            await gate.WaitAsync(ct);
            try
            {
                await bus.InvokeAsync(new CleanupExpiredImportSources(), ct);
                await using var input = upload.OpenReadStream();
                return Results.Ok(await bus.InvokeAsync<WikiImportReport>(new PreviewImport(input, options), ct));
            }
            finally
            {
                gate.Release();
            }
        }).WithName("PreviewWikiJsImport").Accepts<WikiImportUpload>("multipart/form-data").Produces<WikiImportReport>()
            .WithMetadata(new RequestSizeLimitAttribute(WikiJsArchive.MaximumArchiveBytes + 65536),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = WikiJsArchive.MaximumArchiveBytes + 65536, ValueLengthLimit = 8192 });
        group.MapPost("/{id:guid}/commit", async (Guid id, ConfirmWikiImport request, IMessageBus bus, ImportWorkGate gate, CancellationToken ct) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return Results.Ok(await bus.InvokeAsync<WikiImportReport>(new CommitImport(id, request), ct));
            }
            finally
            {
                gate.Release();
            }
        }).WithName("ConfirmWikiJsImport").Produces<WikiImportReport>();
        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<WikiImportReport>(new GetImportReport(id), ct)))
            .WithName("GetWikiJsImport").Produces<WikiImportReport>();
        group.MapGet("/{id:guid}/source", async (Guid id, HttpContext context, IMessageBus bus, ContentImportFiles files, CancellationToken ct) =>
        {
            var report = await bus.InvokeAsync<WikiImportReport>(new GetImportReport(id), ct);
            if (report.CompletedAt is null && report.ExpiresAt < DateTime.UtcNow)
            {
                throw new ApiException(410, "Zdroj náhledu vypršel.");
            }
            Stream stream;
            try
            {
                stream = files.Open(id);
            }
            catch (FileNotFoundException)
            {
                throw new ApiException(410, "Zdrojový ZIP již není v úložišti dostupný.");
            }
            context.Response.Headers.CacheControl = "no-store";
            return new RevalidatingDownload(Results.File(stream, "application/zip", "wiki-js-source-" + id.ToString("N") + ".zip"), stream,
                async token => (await bus.InvokeAsync<DownloadLease>(new RevalidateImportDownload(id, true), token)).Until);
        }).WithName("DownloadWikiJsImportSource").Produces<Stream>(200, "application/zip");
        group.MapGet("/{id:guid}/report.json", async (Guid id, HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            var report = await bus.InvokeAsync<WikiImportReport>(new GetImportReport(id), ct);
            context.Response.Headers.CacheControl = "no-store";
            var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(report, Json), writable: false);
            return new RevalidatingDownload(Results.File(stream, "application/json", "wiki-js-import-" + id.ToString("N") + ".json"), stream,
                async token => (await bus.InvokeAsync<DownloadLease>(new RevalidateImportDownload(id, false), token)).Until);
        }).WithName("DownloadWikiJsImportReport").Produces<WikiImportReport>(200, "application/json");
    }
}
