using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.ContentImport;

public sealed class ContentImportHttpTests
{
    [PostgreSqlFact]
    public async Task Wiki_js_preview_commit_and_report_round_trip_over_http_and_reject_anonymous_callers()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_import_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var importRoot = site.Services.GetRequiredService<ContentImportFiles>().Root;
        Assert.True(importRoot.StartsWith(site.DataPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"Import test storage {importRoot} escapes its isolated directory {site.DataPath}.");

        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/imports/" + Guid.NewGuid().ToString("D"))).StatusCode);

        using var zip = ContentImportTests.Zip(("en/start.md", "# Start"), ("en/next.md", "# Next"));
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "export.zip");
        // Source keys are unique in the installation, which the shared site shares with other tests.
        content.Add(new StringContent($"{{\"sourceKey\":\"{TestLogin.For("wiki")}\",\"spaceName\":\"Imported\"}}"), "options");
        var csrf = await AtlasHttpSite.Json(await admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/imports/wiki-js/preview") { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var preview = await AtlasHttpSite.Json(await admin.SendAsync(request), HttpStatusCode.OK);
        Assert.True(preview["canCommit"]!.GetValue<bool>());
        var id = preview["id"]!.GetValue<string>();

        var committed = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/imports/{id}/commit",
            new { reviewToken = preview["reviewToken"]!.GetValue<string>(), acknowledgeQuarantine = true, acknowledgeWarnings = true }), HttpStatusCode.OK);
        Assert.NotNull(committed["completedAt"]);

        var fetched = await AtlasHttpSite.Json(await admin.GetAsync($"/api/admin/imports/{id}"), HttpStatusCode.OK);
        Assert.Equal(committed["spaceId"]!.GetValue<string>(), fetched["spaceId"]!.GetValue<string>());

        var source = await admin.GetAsync($"/api/admin/imports/{id}/source");
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        Assert.Equal("application/zip", source.Content.Headers.ContentType!.MediaType);

        var report = await admin.GetAsync($"/api/admin/imports/{id}/report.json");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("application/json", report.Content.Headers.ContentType!.MediaType);

        // Cookie session without the CSRF header is rejected before the import runs.
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/imports/{id}/commit")
        {
            Content = JsonContent.Create(new { reviewToken = "x", acknowledgeQuarantine = true, acknowledgeWarnings = true })
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(noCsrf)).StatusCode);
    }
}
