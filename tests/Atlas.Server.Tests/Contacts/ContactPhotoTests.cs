using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Contacts;

/// <summary>The logo or photo of a contact (Atlas 3.5 package G, migration 2026110530): uploaded by the web (PUT /api/contacts/{id}/photo)
/// and MCP (atlas_upsert_contact photoBase64) through one handler, named by the contact's photo field, read by whoever may read the contact,
/// PNG, JPEG or WebP only; the table comes empty over existing contacts and goes with a contact's envelope.</summary>
public sealed class ContactPhotoTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

    [Fact]
    public void Only_a_small_png_jpeg_or_webp_is_a_picture_and_its_address_is_the_one_relative_photo_a_contact_holds()
    {
        var (mimeType, sha256) = ContactPhotos.Validate(Png);
        Assert.Equal(("image/png", Convert.ToHexStringLower(SHA256.HashData(Png))), (mimeType, sha256));
        Assert.Equal("invalid_photo", Assert.Throws<ApiException>(() => ContactPhotos.Validate(Svg)).Code);
        Assert.Equal(413, Assert.Throws<ApiException>(() => ContactPhotos.Validate(new byte[ContactPhotos.MaximumBytes + 1])).Status);

        var id = Guid.NewGuid();
        var url = ContactPhotos.Url(id, sha256);
        Assert.Equal($"/api/contacts/{id:D}/photo?v={sha256[..16]}", url);
        // Only the contact's own picture: never another contact's, and a contact not created yet has none.
        var current = new JsonObject { ["name"] = "DODAVATEL" };
        Assert.Equal(url, ContactFields.Apply(current, new JsonObject { ["photo"] = url }, id).Data["photo"]!.GetValue<string>());
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Apply(current, new JsonObject { ["photo"] = url }, Guid.NewGuid()));
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(new JsonObject { ["name"] = "DODAVATEL", ["photo"] = url }));
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(new JsonObject { ["name"] = "DODAVATEL", ["photo"] = "/api/pages/x" }));
        Assert.Throws<ObjectChangeRejected>(() => ContactFields.Create(new JsonObject { ["name"] = "DODAVATEL", ["photo"] = "javascript:alert(1)" }));
    }

    [PostgreSqlFact]
    public async Task The_web_and_mcp_upload_one_picture_its_reader_gets_and_a_merge_carries_it()
    {
        await using var f = await McpSite.CreateAsync();
        await f.AdminTokenAsync("read");
        var (_, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var agent = await f.ClientAsync(secret);
        var dodavatel = await ContactAsync(f, """{"name":"DODAVATEL","website":"https://vendor.example.test"}""");

        // MCP: the hash guards the transfer, the image its type.
        var sha = Convert.ToHexStringLower(SHA256.HashData(Png));
        Assert.Equal("hash_mismatch", ErrorCode(await CallAsync(agent, "atlas_upsert_contact", new { contactId = dodavatel, contact = new { }, photoBase64 = Convert.ToBase64String(Png), photoSha256 = new string('0', 64) })));
        Assert.Equal("invalid_photo", ErrorCode(await CallAsync(agent, "atlas_upsert_contact",
            new { contactId = dodavatel, contact = new { }, photoBase64 = Convert.ToBase64String(Svg), photoSha256 = Convert.ToHexStringLower(SHA256.HashData(Svg)) })));
        var set = await CallAsync(agent, "atlas_upsert_contact", new { contactId = dodavatel, contact = new { }, photoBase64 = Convert.ToBase64String(Png), photoSha256 = sha });
        Assert.True(set.IsError != true, Text(set));
        var url = $"/api/contacts/{dodavatel}/photo?v={sha[..16]}";
        Assert.Equal(url, Structured(set)["contact"]!["photo"]!.GetValue<string>());
        Assert.Equal(url, Structured(await CallAsync(agent, "atlas_get_contact", new { contactId = dodavatel }))["contact"]!["photo"]!.GetValue<string>());

        // The reader gets the bytes, kept for good under the current version only.
        var read = await f.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(("image/png", true), (read.Content.Headers.ContentType!.MediaType, read.Headers.CacheControl!.ToString().Contains("immutable", StringComparison.Ordinal)));
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("immutable", (await f.Admin.GetAsync($"/api/contacts/{dodavatel}/photo?v=0000000000000000")).Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Admin.GetAsync($"/api/contacts/{Guid.NewGuid()}/photo")).StatusCode);

        // The web: one file in the field file; an SVG is refused and changes nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(f, dodavatel, Svg, "image/svg+xml")).StatusCode);
        var jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");
        var uploaded = await AtlasHttpSite.Json(await UploadAsync(f, dodavatel, jpeg, "image/jpeg"), HttpStatusCode.OK);
        var jpegUrl = uploaded["photo"]!.GetValue<string>();
        Assert.NotEqual(url, jpegUrl);
        Assert.Equal(jpeg, await (await f.Admin.GetAsync(jpegUrl)).Content.ReadAsByteArrayAsync());

        // A merge into a contact without a picture gives it a copy under its own address.
        // An address of its own: the shared site's directory refuses a second contact of an address.
        var target = await ContactAsync(f, $$"""{"name":"DODAVATEL s.r.o.","emails":["info-{{Guid.NewGuid():N}}@vendor.example.test"]}""");
        Assert.True((await CallAsync(agent, "atlas_merge_contacts", new { sourceId = dodavatel, targetId = target, confirm = true })).IsError != true);
        var merged = Structured(await CallAsync(agent, "atlas_get_contact", new { contactId = target }))["contact"]!["photo"]!.GetValue<string>();
        Assert.StartsWith($"/api/contacts/{target}/photo?v=", merged, StringComparison.Ordinal);
        Assert.Equal(jpeg, await (await f.Admin.GetAsync(merged)).Content.ReadAsByteArrayAsync());
    }

    /// <summary>Review of 3.5: the browser's current copy gets 304; a photo field naming another contact's picture is refused; whoever may
    /// not read the contact gets 404, a reader's upload 403 on the web and an error over MCP; a removed picture is gone, its row too (no
    /// backup keeps it).</summary>
    [PostgreSqlFact]
    public async Task Only_who_may_change_a_contact_sets_its_picture_and_a_removed_one_is_gone()
    {
        await using var f = await McpSite.CreateAsync();
        await f.AdminTokenAsync("read");
        var (_, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var agent = await f.ClientAsync(secret);
        var dodavatel = await ContactAsync(f, """{"name":"DODAVATEL"}""");
        var vendor = await ContactAsync(f, """{"name":"JINÝ DODAVATEL"}""");
        var url = (await AtlasHttpSite.Json(await UploadAsync(f, dodavatel, Png, "image/png"), HttpStatusCode.OK))["photo"]!.GetValue<string>();
        async Task<long> RowsAsync(string contact) => (long)(await f.Site.ScalarAsync("""SELECT count(*) FROM "ContactPhoto" WHERE "ContactId"=@id""", ("id", Guid.Parse(contact))))!;

        using (var current = new HttpRequestMessage(HttpMethod.Get, url))
        {
            current.Headers.IfNoneMatch.Add(new EntityTagHeaderValue('"' + Convert.ToHexStringLower(SHA256.HashData(Png)) + '"'));
            Assert.Equal(HttpStatusCode.NotModified, (await f.Admin.SendAsync(current)).StatusCode);
        }
        Assert.Equal("invalid_object_change", ErrorCode(await CallAsync(agent, "atlas_upsert_contact", new { contactId = vendor, contact = new { photo = url } })));

        var (reader, _) = await f.UserAsync("reader", "write");
        var personal = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "private", data = JsonNode.Parse("""{"name":"Soukromý"}""") }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var personalUrl = (await AtlasHttpSite.Json(await UploadAsync(f, personal, Png, "image/png"), HttpStatusCode.OK))["photo"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(personalUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(f, vendor, Png, "image/png", reader)).StatusCode);
        var readerAgent = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"])).Secret);
        var refused = await CallAsync(readerAgent, "atlas_upsert_contact",
            new { contactId = vendor, contact = new { }, photoBase64 = Convert.ToBase64String(Png), photoSha256 = Convert.ToHexStringLower(SHA256.HashData(Png)) });
        Assert.True(refused.IsError, Text(refused));
        Assert.Equal(0L, await RowsAsync(vendor));

        var removed = await CallAsync(agent, "atlas_upsert_contact", new { contactId = dodavatel, contact = new { photo = (string?)null } });
        Assert.True(removed.IsError != true, Text(removed));
        Assert.Equal(0L, await RowsAsync(dodavatel));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Admin.GetAsync(url)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task The_migration_leaves_existing_contacts_as_they_are_and_a_picture_goes_with_its_contact()
    {
        const string Writer = "00000000-0000-0000-0000-000000000001", Contact = "00000000-0000-0000-0000-0000000000c1";
        await using var schema = await DisposableSchema.CreateAsync("atlas_contact_photo_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110530);
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{Writer}}','writer','Writer',false,true,'stamp');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              ('{{Contact}}','atlas.contact','wiki',NULL,NULL,1,now(),'{{Writer}}',now(),'DODAVATEL','{"name":"DODAVATEL","photo":"https://example.com/logo.png"}');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("https://example.com/logo.png", await ScalarAsync(schema.ConnectionString, """SELECT "Data"->>'photo' FROM "ContentResource" """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ContactPhoto" """));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            $"""INSERT INTO "ContactPhoto" VALUES ('{Contact}','image/svg+xml','{new string('a', 64)}','\x3c')"""));
        Assert.Equal("CK_ContactPhoto_MimeType", refused.ConstraintName);
        await ExecuteAsync(schema.ConnectionString, $"""INSERT INTO "ContactPhoto" VALUES ('{Contact}','image/png','{new string('a', 64)}','\x89')""");
        await ExecuteAsync(schema.ConnectionString, """DELETE FROM "ContentResource" """);
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ContactPhoto" """));
    }

    private static async Task<HttpResponseMessage> UploadAsync(McpSite f, string contact, byte[] bytes, string type, HttpClient? client = null)
    {
        client ??= f.Admin;
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(file, "file", "logo");
        var csrf = await AtlasHttpSite.Json(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/contacts/{contact}/photo") { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        return await client.SendAsync(request);
    }

    private static async Task<string> ContactAsync(McpSite f, string data) => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
        new { typeId = "atlas.contact", scope = "wiki", data = JsonNode.Parse(data) }), HttpStatusCode.Created))["id"]!.GetValue<string>();

    /// <summary>Applies the embedded scripts below a version, with their journal, as the previous release left the schema.</summary>
    private static async Task ApplyBeforeAsync(string connectionString, long version)
    {
        await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < version).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
            await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
