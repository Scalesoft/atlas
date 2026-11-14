using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Entities;

/// <summary>What HTTP and MCP add to the entity query: the routes on the web and /api/v1, the export formats, and G5 on MCP.</summary>
public sealed class EntityHttpTests
{
    private static string Card(string email) => """{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková","emails":[""" + JsonSerializer.Serialize(email) + "]}]}}}";

    [PostgreSqlFact]
    public async Task Web_api_token_and_mcp_list_the_same_contacts_and_mcp_leaves_an_excluded_space_out()
    {
        await using var f = await McpSite.CreateAsync();
        // The entity list is the whole instance's: the test's person has an address of its own and is asked for by it.
        var email = $"jana-{Guid.NewGuid():N}@example.com";
        var identity = "identity=email:" + email;
        var space = await f.SpaceAsync("Lidé");
        var hidden = await f.SpaceAsync("Mimo AI");
        var (reader, readerId) = await f.UserAsync("reader");
        await f.GrantAsync(space, readerId, "Reader");
        await f.GrantAsync(hidden, readerId, "Reader");
        var (page, _) = await PageAsync(f.Admin, space, "Tým", Rich(Heading(2, "tym", "Tým"), Card(email)), "Tým");
        await PageAsync(f.Admin, hidden, "Skrytý tým", Rich(Card(email)), "Skrytý");
        await f.ExcludeFromAiAsync(hidden);
        // The site's outbox delivers in memory, after the commit: wait for both pages' published contacts.
        await OutboxTests.WaitForAsync(f.Site.ConnectionString,
            $"""SELECT CASE WHEN count(DISTINCT "ResourceId") >= 2 THEN 1 ELSE 0 END FROM "EntityOccurrence" WHERE "RevisionKind" = 'published' AND "IdentityKey" = 'email:{email}'""");

        var web = await AtlasHttpSite.Json(await reader.GetAsync("/api/entities/contact?scope=instance&" + identity), HttpStatusCode.OK);
        Assert.Equal(["items", "total", "page", "pageSize"], web.AsObject().Select(property => property.Key));
        var item = web["items"]![0]!;
        Assert.Equal(["identityKey", "kind", "attributes", "sources", "sourceCount", "contactId"], item.AsObject().Select(property => property.Key));
        Assert.Equal(["resourceId", "resourceType", "title", "spaceId", "spaceName", "anchor", "revisionKind"], item["sources"]![0]!.AsObject().Select(property => property.Key));
        Assert.Equal(2, item["sources"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/entities/contact?scope=space")).StatusCode);

        var vcard = await reader.GetAsync($"/api/entities/contact/export?scope=space&id={space}&format=vcard");
        Assert.Equal("text/vcard", vcard.Content.Headers.ContentType!.MediaType);
        Assert.Contains("FN:Jana Nováková", await vcard.Content.ReadAsStringAsync());
        var csv = await reader.GetAsync("/api/entities/contact/export");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);

        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(reader, f.Site.Password, ["read"], ["rest"])).Secret);
        Assert.Equal(web.ToJsonString(), (await AtlasHttpSite.Json(await api.GetAsync("/api/v1/entities/contact?" + identity), HttpStatusCode.OK)).ToJsonString());

        var mcp = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read"], ["mcp"])).Secret);
        var listed = Structured(await CallAsync(mcp, "atlas_list_entities", new { kind = "contact", scope = "instance", identity = "email:" + email }));
        var source = listed["items"]![0]!["sources"]!.AsArray().Single()!;
        Assert.Equal((page, "tym"), (source["resourceId"]!.GetValue<string>(), source["anchor"]!.GetValue<string>()));
    }
}
