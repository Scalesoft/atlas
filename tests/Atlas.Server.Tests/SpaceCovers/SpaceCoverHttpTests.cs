using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Spaces;
using Xunit;

namespace Atlas.Server.Tests.SpaceCovers;

public sealed class SpaceCoverHttpTests
{
    private static readonly byte[] Png = SpaceCoverHandlerTests.Png;

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Multipart_upload_needs_CSRF_and_the_image_is_served_immutable_under_its_version()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_cover_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var id = await AtlasHttpSite.OwnSpaceAsync(admin);
        using var member = await SpacesHttpTests.MemberAsync(site, admin, id, "Reader");
        Assert.Equal("Auto", (await SpacesHttpTests.ListedAsync(member, id))["cover"]!["source"]!.GetValue<string>());
        var path = $"/api/spaces/{id}/cover";
        using (var noToken = Multipart(Png, "card.png", "image/png"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(path, noToken)).StatusCode);
        }
        var csrf = (await Json(await admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK))["token"]!.GetValue<string>();
        // The declared type and the name count for nothing: an SVG sent as PNG is refused, a PNG sent as SVG is stored as PNG.
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, path, csrf, "<svg onload='alert(1)'/>"u8.ToArray(), "card.png", "image/png")).StatusCode);
        var tooLarge = new byte[SpaceCoverImage.MaximumBytes + 1];
        Png.CopyTo(tooLarge, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put(admin, path, csrf, tooLarge, "big.png", "image/png")).StatusCode);
        var uploaded = await Json(await Put(admin, path, csrf, Png, "card.svg", "image/svg+xml"), HttpStatusCode.OK);
        var version = uploaded["version"]!.GetValue<string>();
        Assert.True(uploaded["tinted"]!.GetValue<bool>());
        Assert.Equal(uploaded.ToJsonString(), (await SpacesHttpTests.ListedAsync(member, id))["cover"]!.ToJsonString());

        var image = await admin.GetAsync($"{path}?v={version}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        var caching = image.Headers.CacheControl!;
        Assert.Equal((true, TimeSpan.FromDays(365)), (caching.Private, caching.MaxAge!.Value));
        Assert.Contains(caching.Extensions, x => x.Name == "immutable");
        Assert.Equal("nosniff", Assert.Single(image.Headers.GetValues("X-Content-Type-Options")));
        using (var conditional = new HttpRequestMessage(HttpMethod.Get, $"{path}?v={version}"))
        {
            conditional.Headers.IfNoneMatch.Add(image.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await admin.SendAsync(conditional)).StatusCode);
        }
        // A list loaded before a change asks with an old version: it gets the current image, not cached under the old address.
        Assert.True((await admin.GetAsync($"{path}?v=0000000000000000")).Headers.CacheControl!.NoCache);

        var untinted = await Json(await Send(admin, HttpMethod.Patch, path, new { tinted = false }), HttpStatusCode.OK);
        Assert.Equal((version, false), (untinted["version"]!.GetValue<string>(), untinted["tinted"]!.GetValue<bool>()));
        var files = site.Services.GetRequiredService<AttachmentFiles>();
        var previous = await CurrentFileAsync(site, id);
        var replaced = await Json(await Put(admin, path, csrf, SpaceCoverHandlerTests.Jpeg, "photo.jpg", "image/jpeg"), HttpStatusCode.OK);
        Assert.False(replaced["tinted"]!.GetValue<bool>());
        Assert.False(File.Exists(files.PathFor(previous)));
        var current = await CurrentFileAsync(site, id);
        Assert.True(File.Exists(files.PathFor(current)));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Delete, path)).StatusCode);
        Assert.False(File.Exists(files.PathFor(current)));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path)).StatusCode);
        Assert.Equal("Auto", (await Json(await admin.GetAsync($"/api/spaces/{id}/settings"), HttpStatusCode.OK))["cover"]!["source"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Choice_and_match_bind_their_bodies_and_answer_whoever_may_not_ask()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_cover_choice_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var id = await AtlasHttpSite.OwnSpaceAsync(admin);
        using var member = await SpacesHttpTests.MemberAsync(site, admin, id, "Reader");
        var chosen = await Json(await Send(admin, HttpMethod.Put, $"/api/spaces/{id}/cover/choice", new { source = "Catalog", catalogKey = "it-backup" }), HttpStatusCode.OK);
        Assert.Equal("""{"source":"Catalog","catalogKey":"it-backup","version":null,"tinted":true}""", chosen.ToJsonString());
        Assert.Equal(chosen.ToJsonString(), (await SpacesHttpTests.ListedAsync(member, id))["cover"]!.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Put, $"/api/spaces/{id}/cover/choice", new { source = "Catalog", catalogKey = "no-such-drawing" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(admin, HttpMethod.Put, $"/api/spaces/{Guid.NewGuid()}/cover/choice", new { source = "None" })).StatusCode);

        var match = await Json(await Send(admin, HttpMethod.Post, "/api/space-covers/match", new { name = "Zálohy databází", description = "", icon = "book-open" }), HttpStatusCode.OK);
        Assert.Equal("it-backup", match["catalogKey"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Post, "/api/space-covers/match", new { name = new string('a', 201), description = "", icon = "book-open" })).StatusCode);
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Post, "/api/space-covers/match", new { name = "Zálohy databází", description = "", icon = "book-open" })).StatusCode);
    }

    private static async Task<Guid> CurrentFileAsync(AtlasHttpSite site, string spaceId) =>
        (Guid)(await site.ScalarAsync("SELECT \"FileId\" FROM \"SpaceCover\" WHERE \"SpaceId\"=@space AND \"ActiveTo\" IS NULL", ("space", Guid.Parse(spaceId))))!;

    private static async Task<HttpResponseMessage> Put(HttpClient client, string path, string csrf, byte[] bytes, string name, string type)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = Multipart(bytes, name, type) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, string name, string type)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);
    private static Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected) => AtlasHttpSite.Json(response, expected);
}
