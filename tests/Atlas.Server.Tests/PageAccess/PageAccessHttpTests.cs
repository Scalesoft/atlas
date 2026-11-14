using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageAccess;

public sealed class PageAccessHttpTests
{
    private static Task<int> ChangeRole(AtlasHttpSite site, Guid actor, Guid space, int role) => site.ExecuteAsync("""
        UPDATE "SpaceGrant" SET "ActiveTo"=now() WHERE "UserId"=@actor AND "SpaceId"=@space AND "ActiveTo" IS NULL;
        INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","Role","ActiveFrom") VALUES (gen_random_uuid(),@space,@actor,@role,now());
        """, ("actor", actor), ("space", space), ("role", role));

    [PostgreSqlFact]
    public async Task Private_draft_explanation_obeys_real_page_visibility_and_current_identity()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_private_access_");
        using var admin = site.Client();
        var actor = await site.CompleteSetupAsync(admin);
        var actorId = actor["id"]!.GetValue<Guid>();
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Private", contentJson = Rich, markdown = "Private" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<Guid>();
        var path = $"/api/pages/{pageId}/access";
        var privateAccess = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.False(privateAccess["isPublished"]!.GetValue<bool>());
        Assert.False(privateAccess["myAccess"]!["canReadPublished"]!.GetValue<bool>());
        Assert.True(privateAccess["myAccess"]!["canReadDraft"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "?offset=-1")).StatusCode);
        await site.ExecuteAsync("""
            UPDATE "UserAccount" SET "IsWikiAdmin"=false WHERE "Id"=@actor;
            """, ("actor", actorId), ("space", spaceId));
        await ChangeRole(site, actorId, spaceId, 2);
        var owner = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.True(owner["myAccess"]!["canEdit"]!.GetValue<bool>());
        Assert.Empty(owner["items"]!.AsArray());
        await ChangeRole(site, actorId, spaceId, 1);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path)).StatusCode);
        await ChangeRole(site, actorId, spaceId, 3);
        var other = Guid.NewGuid();
        var otherLogin = TestLogin.For("other");
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount" ("Id","Username","DisplayName","SecurityStamp","IsWikiAdmin","IsActive")
                VALUES (@other,@login,'Other','other-stamp',false,true);
            UPDATE "Page" SET "OwnerId"=@other WHERE "Id"=@page;
            """, ("other", other), ("page", pageId), ("login", otherLogin));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path)).StatusCode);
        await site.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=true WHERE \"Id\"=@actor", ("actor", actorId));
        await site.ExecuteAsync("""
            INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","Role","ActiveFrom")
                VALUES (gen_random_uuid(),@space,@other,1,now());
            """, ("space", spaceId), ("other", other));
        // The manager's list shows everyone a grant reaches, with what the private draft rule still allows them. The list puts
        // every wiki administrator of the shared installation first, so it is narrowed to this test's account.
        var directory = await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=" + otherLogin), HttpStatusCode.OK);
        var listed = Assert.Single(directory["items"]!.AsArray(), x => x!["userId"]!.GetValue<Guid>() == other)!;
        Assert.False(listed["canReadDraft"]!.GetValue<bool>());
        var probe = await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=" + otherLogin.ToUpperInvariant()), HttpStatusCode.OK);
        var deniedOwner = Assert.Single(probe["items"]!.AsArray())!;
        Assert.Equal("Reader", deniedOwner["role"]!.GetValue<string>());
        Assert.False(deniedOwner["canReadDraft"]!.GetValue<bool>());
        Assert.False(deniedOwner["canReadPublished"]!.GetValue<bool>());
        Assert.False(deniedOwner["canEdit"]!.GetValue<bool>());
        await site.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='changed' WHERE \"Id\"=@actor", ("actor", actorId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(path)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Access_explains_sources_and_paginates_people_without_disclosing_the_directory_to_readers()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_access_");
        using var admin = site.Client();
        var actor = await site.CompleteSetupAsync(admin);
        var actorId = actor["id"]!.GetValue<Guid>();
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Access", contentJson = Rich, markdown = "Access" }), HttpStatusCode.OK);
        var path = $"/api/pages/{page["id"]!.GetValue<Guid>()}/access";
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            $"/api/pages/{page["id"]!.GetValue<Guid>()}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        var personId = Guid.NewGuid();
        var excludedId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var outsiderLogin = TestLogin.For("outsider");
        var personLogin = TestLogin.For("person");
        var editors = TestLogin.For("Editors");
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount" ("Id","Username","DisplayName","SecurityStamp","IsWikiAdmin","IsActive") VALUES
                (@person,@personLogin,'Person','person-stamp',false,true),(@excluded,@outsiderLogin,'Outsider','outsider-stamp',false,true);
            INSERT INTO "UserGroup" ("Id","Name") VALUES (@group,@editors);
            INSERT INTO "GroupMember" ("Id","GroupId","UserId","ActiveFrom") VALUES (gen_random_uuid(),@group,@person,now());
            INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","GroupId","Role","ActiveFrom") VALUES
                (gen_random_uuid(),@space,@person,NULL,1,now()),(gen_random_uuid(),@space,NULL,@group,2,now());
            """, ("person", personId), ("excluded", excludedId), ("group", groupId), ("space", spaceId),
            ("personLogin", personLogin), ("outsiderLogin", outsiderLogin), ("editors", editors));
        var access = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        // Every wiki administrator of the shared installation comes first in the list: this test's person is read by its login.
        var people = await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=" + personLogin), HttpStatusCode.OK);
        Assert.True(access["canManage"]!.GetValue<bool>());
        Assert.True(access["myAccess"]!["isWikiAdmin"]!.GetValue<bool>());
        Assert.Contains(access["myAccess"]!["sources"]!.AsArray(), source => source!["kind"]!.GetValue<string>() == "global");
        var person = Assert.Single(people["items"]!.AsArray(), x => x!["userId"]!.GetValue<Guid>() == personId)!;
        Assert.Equal("Writer", person["role"]!.GetValue<string>());
        Assert.True(person["canReadDraft"]!.GetValue<bool>());
        Assert.Equal(new[] { "space", "space" }, person["sources"]!.AsArray().Select(x => x!["kind"]!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "", editors }, person["sources"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order().ToArray());
        Assert.DoesNotContain(access["items"]!.AsArray(), x => x!["userId"]!.GetValue<Guid>() == excludedId);
        // The list holds only people a grant reaches; the directory search of the add wizard is a separate endpoint.
        var probe = await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=" + outsiderLogin), HttpStatusCode.OK);
        Assert.Empty(probe["items"]!.AsArray());
        var first = await AtlasHttpSite.Json(await admin.GetAsync(path + "?limit=1"), HttpStatusCode.OK);
        Assert.True(first["hasMore"]!.GetValue<bool>());
        var next = await AtlasHttpSite.Json(await admin.GetAsync(path + "?limit=1&offset=1"), HttpStatusCode.OK);
        Assert.NotEqual(first["items"]![0]!["userId"]!.GetValue<Guid>(), next["items"]![0]!["userId"]!.GetValue<Guid>());
        await site.ExecuteAsync("""
            UPDATE "UserAccount" SET "IsWikiAdmin"=false WHERE "Id"=@actor;
            """, ("actor", actorId), ("space", spaceId));
        await ChangeRole(site, actorId, spaceId, 1);
        var own = await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=person"), HttpStatusCode.OK);
        Assert.False(own["canManage"]!.GetValue<bool>());
        Assert.Empty(own["items"]!.AsArray());
        Assert.False(own["myAccess"]!["canReadDraft"]!.GetValue<bool>());
        Assert.True(own["myAccess"]!["canReadPublished"]!.GetValue<bool>());
    }

    [PostgreSqlFact]
    public async Task Management_endpoints_change_grants_and_mode_explain_and_search_only_for_managers()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_access_mgmt_http_");
        using var admin = site.Client();
        var actorId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<Guid>();
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Plán", contentJson = Rich, markdown = "Plán" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<Guid>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        var personId = Guid.NewGuid();
        var janaLogin = TestLogin.For("jana");
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount" ("Id","Username","DisplayName","SecurityStamp","IsWikiAdmin","IsActive") VALUES (@person,@login,'Jana','jana-stamp',false,true);
            """, ("person", personId), ("login", janaLogin));
        var path = $"/api/pages/{pageId}/access";

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path + "/grants",
            new { userId = personId, role = "Reader", expectedVersion = (string?)null, expiresAt = DateTimeOffset.UtcNow.AddDays(7) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path + "/grants",
            new { userId = personId, role = "Owner", expectedVersion = (string?)null })).StatusCode);
        var access = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.Equal("inherit", access["mode"]!.GetValue<string>());
        Assert.Equal("Plán", access["pageTitle"]!.GetValue<string>());
        // Wiki administrators of the shared installation come first in the list: this test's person is read by its login.
        var person = Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=" + janaLogin), HttpStatusCode.OK))["items"]!.AsArray(),
            x => x!["userId"]!.GetValue<Guid>() == personId)!;
        Assert.Equal("page", person["sources"]![0]!["kind"]!.GetValue<string>());
        Assert.NotNull(person["sources"]![0]!["version"]!.GetValue<string>());
        Assert.Equal(1, access["counts"]!["read"]!.GetValue<int>());
        var candidates = await AtlasHttpSite.Json(await admin.GetAsync(path + "/candidates?kind=user&q=" + janaLogin), HttpStatusCode.OK);
        Assert.Equal(personId, Assert.Single(candidates.AsArray())!["id"]!.GetValue<Guid>());
        var explained = await AtlasHttpSite.Json(await admin.GetAsync(path + $"/explain?userId={personId}"), HttpStatusCode.OK);
        Assert.True(explained["canView"]!.GetValue<bool>());
        Assert.False(explained["canEdit"]!.GetValue<bool>());

        var version = access["modeVersion"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path + "/mode",
            new { ownAccess = true, expectedVersion = "2020-01-01T00:00:00.0000000Z" })).StatusCode);
        var mode = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path + "/mode",
            new { ownAccess = true, expectedVersion = version }), HttpStatusCode.OK);
        Assert.Equal("custom", mode["mode"]!.GetValue<string>());

        // A space Reader cannot use management: the own-access page is invisible (404); on a readable page it is told 403.
        await site.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=@actor", ("actor", actorId));
        await ChangeRole(site, actorId, spaceId, 1);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path + $"/explain?userId={personId}")).StatusCode);
        await site.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\"=false WHERE \"Id\"=@page", ("page", pageId));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(path + $"/explain?userId={personId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(path + "/candidates?kind=user")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path + "/grants",
            new { userId = actorId, role = "Admin", expectedVersion = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path + "/mode",
            new { ownAccess = true, expectedVersion = (string?)null })).StatusCode);
        var own = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.False(own["canManage"]!.GetValue<bool>());
        Assert.Empty(own["items"]!.AsArray());
        Assert.Null(own["counts"]);
    }

    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Access"}]}]}}""";
}
