using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;
public sealed class ContentExportHttpTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Cookie_and_personal_read_token_export_ZIP_with_no_store_hash_and_revoke_cleanly()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_export_http_");
        using var admin = site.Client();
        var user = await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich="""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page=await Json(await Send(admin,HttpMethod.Post,"/api/pages",new {spaceId,parentId=(string?)null,title="Export draft",contentJson=rich,markdown="private working"}),HttpStatusCode.OK);
        var path=$"/api/pages/{page["id"]!.GetValue<string>()}/export";
        using var anonymous=site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync(path)).StatusCode);
        using(var response=await admin.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            Assert.Equal("application/zip",response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment",response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var bytes=await response.Content.ReadAsByteArrayAsync();
            Assert.Equal('"'+Convert.ToHexStringLower(SHA256.HashData(bytes))+'"',response.Headers.ETag!.Tag);
            using var zip=new ZipArchive(new MemoryStream(bytes));
            using var reader=new StreamReader(zip.GetEntry("manifest.json")!.Open());
            var manifest=JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Equal("Export draft",manifest["pages"]![0]!["title"]!.GetValue<string>());
            Assert.False(manifest["pages"]![0]!["isPublished"]!.GetValue<bool>());
            Assert.DoesNotContain(zip.Entries,x=>x.FullName.Contains("account",StringComparison.OrdinalIgnoreCase) || x.FullName.Contains("security",StringComparison.OrdinalIgnoreCase));
        }
        await Json(await Send(admin,HttpMethod.Put,$"/api/admin/users/{user["id"]!.GetValue<string>()}/api-access",new{canIssueTokens=true,expectedVersion=(string?)null}),HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var token=await Json(await Send(admin,HttpMethod.Post,"/api/profile/api-tokens",new{name="Export read",scopes=new[]{"read"},expiresInDays=1}),HttpStatusCode.Created);
        using var api=site.Client();
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token["secret"]!.GetValue<string>());
        var apiPath=path.Replace("/api/","/api/v1/",StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK,(await api.GetAsync(apiPath)).StatusCode);
        var description=await Json(await api.GetAsync("/api/v1/openapi.json"),HttpStatusCode.OK);
        foreach(var route in new[]{"/api/v1/pages/{id}/export","/api/v1/spaces/{id}/export"})
            Assert.True(description["paths"]?[route]?["get"]?["responses"]?["200"]?["content"]?["application/zip"] is not null, description["paths"]?[route]?.ToJsonString() ?? "Export path missing");
        Assert.Equal(HttpStatusCode.OK,(await api.GetAsync($"/api/v1/spaces/{spaceId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await api.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await admin.GetAsync(apiPath)).StatusCode);
        Assert.Equal(1, await site.ExecuteAsync("UPDATE \"PersonalApiToken\" SET \"ExpiresAt\" = \"ActiveFrom\" + interval '1 microsecond' WHERE \"Id\" = @id", ("id", Guid.Parse(token["token"]!["id"]!.GetValue<string>()))));
        Assert.Equal(HttpStatusCode.Unauthorized,(await api.GetAsync(apiPath)).StatusCode);
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);
    private static Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected) => AtlasHttpSite.Json(response, expected);
}
