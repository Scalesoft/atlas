using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Tests.Backups;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>T2: every revision and publication carries the transport it came through and, for api and mcp, the token.
/// The channel is derived by the server from the request, never sent by the client.</summary>
public sealed class ContentChannelTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    /// <summary>A page holding <paramref name="text"/>: the stored Markdown is written from the JSON, so each revision differs in it.</summary>
    private static string Text(string text) => Rich.Replace("{\"type\":\"paragraph\"}", "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}", StringComparison.Ordinal);

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Every_revision_path_records_its_channel()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_channel_");
        using var web = site.Client();
        var setup = await site.CompleteSetupAsync(web);
        var ownerId = setup["id"]!.GetValue<string>();
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(web);

        // Cookie: create, save, rename, restore, source edit and publish are all web.
        var page = await Json(await Send(web, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Web", contentJson = Rich, markdown = "web" }), HttpStatusCode.OK);
        var webId = Guid.Parse(page["id"]!.GetValue<string>());
        await Json(await Send(web, HttpMethod.Put, $"/api/pages/{webId}", new { title = "Web", contentJson = Text("web saved"), markdown = "web saved", expectedVersion = 1 }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await Send(web, HttpMethod.Put, $"/api/pages/{webId}/title", new { title = "Web renamed", expectedVersion = 2 })).StatusCode);
        await Json(await Send(web, HttpMethod.Post, $"/api/pages/{webId}/restore", new { revisionId = await FirstRevisionAsync(site, webId), expectedVersion = 3 }), HttpStatusCode.OK);
        await Json(await Send(web, HttpMethod.Put, $"/api/pages/{webId}/source", new { contentJson = Text("source"), markdown = "source", expectedVersion = 4 }), HttpStatusCode.OK);
        await Json(await Send(web, HttpMethod.Post, $"/api/pages/{webId}/publish", new { expectedVersion = 5 }), HttpStatusCode.OK);
        Assert.Equal("1:web:-,2:web:-,3:web:-,4:web:-,5:web:-", await RevisionsAsync(site, webId));
        Assert.Equal("5:web:-", await PublicationsAsync(site, webId));

        // Bearer on /api/v1: the same operations are api and carry the token; the author is still its owner.
        var (tokenId, api) = await IssueTokenAsync(site, web, ownerId, "Claude Code – notebook");
        using (api)
        {
            page = await Json(await api.PostAsJsonAsync("/api/v1/pages", new { spaceId, parentId = (string?)null, title = "Api", markdown = "api" }), HttpStatusCode.Created);
            var apiId = Guid.Parse(page["id"]!.GetValue<string>());
            await Json(await api.PutAsJsonAsync($"/api/v1/pages/{apiId}", new { title = "Api", markdown = "api saved", expectedVersion = 1 }), HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync($"/api/v1/pages/{apiId}/title", new { title = "Api renamed", expectedVersion = 2 })).StatusCode);
            await Json(await api.PostAsJsonAsync($"/api/v1/pages/{apiId}/restore", new { revisionId = await FirstRevisionAsync(site, apiId), expectedVersion = 3 }), HttpStatusCode.OK);
            await Json(await api.PostAsJsonAsync($"/api/v1/pages/{apiId}/publish", new { expectedVersion = 4 }), HttpStatusCode.OK);
            Assert.Equal($"1:api:{tokenId},2:api:{tokenId},3:api:{tokenId},4:api:{tokenId}", await RevisionsAsync(site, apiId));
            Assert.Equal($"4:api:{tokenId}", await PublicationsAsync(site, apiId));
            Assert.Equal(1L, await site.ScalarAsync("SELECT count(DISTINCT \"AuthorId\") FROM \"PageRevision\" WHERE \"PageId\"=@p AND \"AuthorId\"=@a::uuid", ("p", apiId), ("a", ownerId)));

            // The history contract for the web (W2): the owner sees the token by name.
            var history = await Json(await api.GetAsync($"/api/v1/pages/{apiId}/history"), HttpStatusCode.OK);
            var latest = history["items"]![0]!;
            Assert.Equal("api", latest["channel"]!.GetValue<string>());
            Assert.Equal("Claude Code – notebook", latest["apiToken"]!["name"]!.GetValue<string>());
            Assert.False(latest["apiToken"]!["revoked"]!.GetValue<bool>());
            Assert.Equal("api", latest["publicationChannel"]!.GetValue<string>());
        }

        // No default: a write path that forgets the channel cannot insert a revision.
        var missing = await Assert.ThrowsAsync<PostgresException>(() => site.ExecuteAsync("""
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom")
            SELECT gen_random_uuid(),"PageId",99,'Forgotten','{}','',"AuthorId",now() + interval '1 day' FROM "PageRevision" WHERE "PageId"=@p AND "Version"=1
            """, ("p", webId)));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, missing.SqlState);
        Assert.Equal("Channel", missing.ColumnName);
    }

    [PostgreSqlFact]
    public async Task Token_name_is_visible_to_its_owner_and_wiki_admins_only()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_channel_names_");
        var owner = await host.SeedUserAsync("owner");
        var admin = await host.SeedUserAsync("admin", true);
        var colleague = await host.SeedUserAsync("colleague");
        var spaceId = await host.SeedSpaceAsync("Channels", (owner, SpaceRole.Writer), (colleague, SpaceRole.Writer));
        var tokenId = await TestApiTokens.SeedAsync(host, owner, "Claude Code – notebook");
        host.Channel.Current = new RequestChannel(ContentChannel.Api, tokenId);
        host.Actor.Current = owner;
        var page = await host.InvokeAsync<PageDto>(new CreatePage(new(spaceId, null, "Through the API", Rich, "api")));
        await host.InvokeAsync<PageDto>(new PublishPage(page.Id, new(1)));
        host.Channel.Current = RequestChannel.Web;

        foreach (var reader in new[] { owner, admin })
        {
            host.Actor.Current = reader;
            var item = Assert.Single((await host.InvokeAsync<PageHistoryList>(new GetPageHistory(page.Id, null, 20))).Items);
            Assert.Equal("api", item.Channel);
            Assert.Equal(new ApiTokenRef(tokenId, "Claude Code – notebook", false), item.ApiToken);
            Assert.Equal("api", item.PublicationChannel);
            Assert.Equal(tokenId, item.PublicationApiToken?.Id);
            var update = await UpdateOfAsync(host, page.Id);
            Assert.Equal("api", update.PublicationChannel);
            Assert.Equal("Claude Code – notebook", update.PublicationApiToken?.Name);
        }

        // Another writer of the space learns the channel, never the token: the DTO does not carry it at all.
        host.Actor.Current = colleague;
        var hidden = Assert.Single((await host.InvokeAsync<PageHistoryList>(new GetPageHistory(page.Id, null, 20))).Items);
        Assert.Equal(("api", (ApiTokenRef?)null, "api", (ApiTokenRef?)null), (hidden.Channel, hidden.ApiToken, hidden.PublicationChannel, hidden.PublicationApiToken));
        var revision = await host.InvokeAsync<PageHistoryRevision>(new GetPageRevision(page.Id, hidden.Id));
        Assert.Equal(("api", (ApiTokenRef?)null), (revision.Channel, revision.ApiToken));
        var colleagueUpdate = await UpdateOfAsync(host, page.Id);
        Assert.Equal(("api", (ApiTokenRef?)null), (colleagueUpdate.PublicationChannel, colleagueUpdate.PublicationApiToken));

        // A revoked token keeps its name in the history and says so.
        await TestApiTokens.RevokeAsync(host, tokenId);
        host.Actor.Current = owner;
        revision = await host.InvokeAsync<PageHistoryRevision>(new GetPageRevision(page.Id, hidden.Id));
        Assert.Equal(new ApiTokenRef(tokenId, "Claude Code – notebook", true), revision.ApiToken);
    }

    [PostgreSqlFact]
    public async Task Backup_restore_marks_revisions_as_import_without_token()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var admin = new UserAccount { Id = fixture.AdminId };
        var tokenId = await TestApiTokens.SeedAsync(fixture.Host, admin, "Backup agent");
        await fixture.Host.ExecuteAsync("UPDATE \"PageRevision\" SET \"Channel\"='api', \"ApiTokenId\"=:id", tokenId);
        await fixture.Host.ExecuteAsync("UPDATE \"PagePublication\" SET \"Channel\"='api', \"ApiTokenId\"=:id", tokenId);
        using var archive = await fixture.Archive.CreateAsync("full", default);

        // The archive format does not change: no entry carries a channel or a token.
        using (var zip = ZipFile.OpenRead(archive.Path))
        {
            foreach (var entry in zip.Entries.Where(x => x.FullName.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                var json = await reader.ReadToEndAsync();
                Assert.DoesNotContain(tokenId.ToString(), json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\"channel\"", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("apiTokenId", json, StringComparison.OrdinalIgnoreCase);
            }
        }

        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        }
        using var check = fixture.Host.OpenSession();
        var copies = await check.CreateSQLQuery("""
            SELECT 'r:' || "Channel" || ':' || coalesce(CAST("ApiTokenId" AS text),'-') FROM "PageRevision" WHERE "PageId" NOT IN (:page,:trashed)
            UNION ALL
            SELECT 'p:' || "Channel" || ':' || coalesce(CAST("ApiTokenId" AS text),'-') FROM "PagePublication" WHERE "PageId" NOT IN (:page,:trashed)
            """).SetParameter("page", fixture.PageId).SetParameter("trashed", fixture.TrashedPageId).ListAsync<string>();
        Assert.Contains("r:import:-", copies);
        Assert.Contains("p:import:-", copies);
        Assert.All(copies, row => Assert.EndsWith(":import:-", row));
    }

    private static async Task<(Guid TokenId, HttpClient Client)> IssueTokenAsync(AtlasHttpSite site, HttpClient web, string userId, string name)
    {
        await Json(await Send(web, HttpMethod.Put, $"/api/admin/users/{userId}/api-access", new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(web, site.Password);
        var token = await Json(await Send(web, HttpMethod.Post, "/api/profile/api-tokens", new { name, scopes = new[] { "read", "write" }, expiresInDays = 1 }), HttpStatusCode.Created);
        var client = site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        return (Guid.Parse(token["token"]!["id"]!.GetValue<string>()), client);
    }

    /// <summary>The page's row of the actor's "updates": a wiki administrator's list holds every test's publications, newest first,
    /// so it is paged until the page shows up.</summary>
    private static async Task<PersonalLibraryItem> UpdateOfAsync(AtlasTestHost host, Guid pageId)
    {
        for (int? offset = 0; offset is <= 1000; )
        {
            var list = await host.InvokeAsync<PersonalLibraryList>(new ListPersonalLibrary("updates", offset.Value, 50));
            if (list.Items.Where(x => x.Id == pageId).ToArray() is { Length: > 0 } found)
            {
                return Assert.Single(found);
            }
            offset = list.HasMore ? list.NextOffset : null;
        }
        throw new Xunit.Sdk.XunitException($"Page {pageId} is not among the updates.");
    }

    private static async Task<Guid> FirstRevisionAsync(AtlasHttpSite site, Guid pageId)
        => (Guid)(await site.ScalarAsync("SELECT \"Id\" FROM \"PageRevision\" WHERE \"PageId\"=@p AND \"Version\"=1", ("p", pageId)))!;

    private static async Task<string?> RevisionsAsync(AtlasHttpSite site, Guid pageId)
        => (string?)await site.ScalarAsync("""
            SELECT string_agg("Version" || ':' || "Channel" || ':' || coalesce("ApiTokenId"::text,'-'), ',' ORDER BY "Version") FROM "PageRevision" WHERE "PageId"=@p
            """, ("p", pageId));

    private static async Task<string?> PublicationsAsync(AtlasHttpSite site, Guid pageId)
        => (string?)await site.ScalarAsync("""
            SELECT string_agg(r."Version" || ':' || p."Channel" || ':' || coalesce(p."ApiTokenId"::text,'-'), ',' ORDER BY p."ActiveFrom")
            FROM "PagePublication" p JOIN "PageRevision" r ON r."Id"=p."RevisionId" WHERE p."PageId"=@p
            """, ("p", pageId));

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);
    private static Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected) => AtlasHttpSite.Json(response, expected);
}
