using System.Net;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Tests.ContentExport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class ContentPdfHttpTests
{
    [PostgreSqlFact]
    public async Task Unsupported_formats_are_rejected_and_a_busy_renderer_answers_at_once_with_retry_after()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "print.html"), "<html></html>");
        await using var site = await AtlasHttpSite.CreateAsync("atlas_pdf_http_", configureServices: services => services.AddSingleton(provider => new ContentPdfRenderer(
            new AttachmentTests.EnvironmentStub(root) { WebRootPath = root }, provider.GetRequiredService<ContentExportFiles>(), NullLogger<ContentPdfRenderer>.Instance)));
        using var admin = site.Client();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/setup/complete", new { bootstrapToken = site.BootstrapToken, workspaceName = "Příručky", username = "admin", displayName = "Admin", password = site.Password }), HttpStatusCode.OK);
        var spaces = await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var spaceId = spaces[0]!["id"]!.GetValue<string>();
        var rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Žluťoučký kůň – příručka."}]}]}}""";
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Příručka", contentJson = rich, markdown = "Žluťoučký kůň – příručka." }), HttpStatusCode.OK);
        var path = $"/api/pages/{page["id"]!.GetValue<string>()}/export?format=pdf";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path.Replace("format=pdf", "format=docx", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/spaces/{spaceId}/export?format=pdf")).StatusCode);

        // Another export holds the one rendering slot: the answer comes now, not after the reverse proxy's timeout.
        using var held = new ContentPdfRendererTests.BlockingManifestStream(ContentPdfRendererTests.ArchiveBytes());
        var holding = site.Services.GetRequiredService<ContentPdfRenderer>().RenderAsync(ContentPdfRendererTests.Archive(held), "https://wiki.invalid/", PdfCoverDetails.All, CancellationToken.None);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var busy = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(ContentPdfRenderer.RetryAfterSeconds), busy.Headers.RetryAfter?.Delta);
        Assert.Equal(ContentPdfRenderer.BusyCode, (await AtlasHttpSite.Json(busy, HttpStatusCode.ServiceUnavailable))["code"]!.GetValue<string>());
        held.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidDataException>(() => holding);
        Directory.Delete(root, true);
    }
}
