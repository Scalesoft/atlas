using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageAccess;

public sealed class PagePeopleHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"People"}]}]}}""";

    [PostgreSqlFact]
    public async Task Editors_find_active_accounts_for_contacts_and_readers_are_refused()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_people_");
        using var admin = site.Client();
        var actor = await site.CompleteSetupAsync(admin);
        var actorId = actor["id"]!.GetValue<Guid>();
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "People", contentJson = Rich, markdown = "People" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<Guid>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish",
            new { expectedVersion = 1 }), HttpStatusCode.OK);
        var jana = Guid.NewGuid();
        var retired = Guid.NewGuid();
        // The directory is the whole installation's: a token of this test in both logins and e-mail domains narrows it to these two.
        var token = Guid.NewGuid().ToString("N")[..10];
        // Neither has any grant: the directory is every active account, not the page's access list.
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount" ("Id","Username","DisplayName","Email","SecurityStamp","IsWikiAdmin","IsActive") VALUES
                (@jana,'jnovakova-' || @token,'Jana Nováková','jana@' || @token || '.example.com','jana-stamp',false,true),
                (@retired,'jretired-' || @token,'Jana Retired','retired@' || @token || '.example.com','retired-stamp',false,false);
            """, ("jana", jana), ("retired", retired), ("token", token));
        var path = $"/api/pages/{pageId}/people";

        var found = await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=JANA%20" + token.ToUpperInvariant()), HttpStatusCode.OK);
        var person = Assert.Single(found.AsArray())!;
        Assert.Equal(jana, person["id"]!.GetValue<Guid>());
        Assert.Equal("Jana Nováková", person["name"]!.GetValue<string>());
        Assert.Equal($"jana@{token}.example.com", person["email"]!.GetValue<string>());
        Assert.Equal("jnovakova-" + token, person["username"]!.GetValue<string>());
        Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync(path + "?q=jana%40" + token), HttpStatusCode.OK)).AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "?q=" + new string('x', 201))).StatusCode);

        var one = await AtlasHttpSite.Json(await admin.GetAsync($"{path}/{jana}"), HttpStatusCode.OK);
        Assert.Equal("Jana Nováková", one["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{path}/{retired}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{path}/{Guid.NewGuid()}")).StatusCode);

        // A reader of the page cannot mention anyone, so the directory stays closed to them.
        await site.ExecuteAsync("""
            UPDATE "UserAccount" SET "IsWikiAdmin"=false WHERE "Id"=@actor;
            UPDATE "SpaceGrant" SET "ActiveTo"=now() WHERE "UserId"=@actor AND "SpaceId"=@space AND "ActiveTo" IS NULL;
            INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","Role","ActiveFrom") VALUES (gen_random_uuid(),@space,@actor,1,now());
            """, ("actor", actorId), ("space", spaceId));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(path + "?q=jana")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{path}/{jana}")).StatusCode);
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
    }
}
