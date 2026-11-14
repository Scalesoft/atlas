using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Linking.Lexicon.Queries;
using Atlas.Server.Platform.Hosting;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Linking;

public sealed class SpaceLexiconHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Text"}]}]}}""";

    [PostgreSqlFact]
    public async Task A_reader_gets_only_what_it_may_read_with_aliases_and_a_revalidating_etag()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_lexicon_");
        using var admin = site.Client();
        var adminId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<Guid>();
        var space = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var hidden = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin, "Tajný"));
        var published = await PageAsync(admin, space, "Veřejná stránka", publish: true);
        await PageAsync(admin, space, "Koncept správce", publish: false);
        var (reader, readerId) = await PersonAsync(site, admin, "mmusil", "Marek Musil");
        // The wiki's contacts are in every lexicon: the names of the test's own contacts carry a token no other test uses.
        var token = Guid.NewGuid().ToString("N")[..8];
        string Named(string name) => $"{name} {token}";
        var martin = Guid.NewGuid();
        var jana = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        await site.ExecuteAsync("""
            INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","Role","ActiveFrom") VALUES (gen_random_uuid(),@space,@reader,1,now());
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              (@martin,'atlas.contact','wiki',NULL,NULL,1,now(),@admin,now(),@martinName,
                jsonb_build_object('name',CAST(@martinName AS text),'aliases',jsonb_build_array('MMu','Mára'),'organization','Modelsoft','title','PM','directoryId',CAST(@reader AS text))),
              (@jana,'atlas.contact','space',@space,NULL,1,now(),@admin,now(),@janaName,jsonb_build_object('name',CAST(@janaName AS text))),
              (gen_random_uuid(),'atlas.contact','space',@hidden,NULL,1,now(),@admin,now(),@hiddenName,jsonb_build_object('name',CAST(@hiddenName AS text))),
              (gen_random_uuid(),'atlas.contact','private',NULL,@admin,1,now(),@admin,now(),@privateName,jsonb_build_object('name',CAST(@privateName AS text))),
              (@deleted,'atlas.contact','wiki',NULL,NULL,1,now(),@admin,now(),@deletedName,jsonb_build_object('name',CAST(@deletedName AS text)));
            UPDATE "ContentResource" SET "TrashedAt"=now() WHERE "Id"=@deleted;
            """, ("space", space), ("hidden", hidden), ("reader", readerId), ("admin", adminId), ("martin", martin), ("jana", jana), ("deleted", deleted),
            ("martinName", Named("Marek Musil")), ("janaName", Named("Jana Nováková")), ("hiddenName", Named("Skrytý Kontakt")),
            ("privateName", Named("Soukromý Kontakt")), ("deletedName", Named("Smazaný Kontakt")));
        var path = $"/api/spaces/{space}/lexicon";

        // The same answer revalidates by its ETag. The version hashes the wiki's contacts too, which other tests of the shared site
        // write: a read answered 200 again is only a changed version, and two reads in a row agree within a few tries.
        HttpResponseMessage first;
        JsonNode lexicon;
        for (var attempt = 0; ; attempt++)
        {
            first = await reader.GetAsync(path);
            lexicon = await AtlasHttpSite.Json(first, HttpStatusCode.OK);
            using var revalidated = new HttpRequestMessage(HttpMethod.Get, path);
            revalidated.Headers.IfNoneMatch.Add(first.Headers.ETag!);
            var again = await reader.SendAsync(revalidated);
            if (again.StatusCode == HttpStatusCode.NotModified)
            {
                break;
            }
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.NotEqual(first.Headers.ETag!.Tag, again.Headers.ETag!.Tag);
            Assert.True(attempt < 5, "The lexicon never revalidated.");
        }
        Assert.Equal($"\"{lexicon["version"]!.GetValue<string>()}\"", first.Headers.ETag!.Tag);
        Assert.False(lexicon["truncated"]!.GetValue<bool>());
        Assert.Equal([published], lexicon["pages"]!.AsArray().Select(page => page!["id"]!.GetValue<Guid>()));
        var people = lexicon["people"]!.AsArray().ToDictionary(person => person!["name"]!.GetValue<string>(), person => person!);
        Assert.DoesNotContain(Named("Skrytý Kontakt"), people.Keys);
        Assert.DoesNotContain(Named("Soukromý Kontakt"), people.Keys);
        Assert.DoesNotContain(Named("Smazaný Kontakt"), people.Keys);
        // The reader's account is its directory contact: one person, with the contact's aliases (no initials stored).
        var me = Assert.Single(people.Values, person => person["userId"]?.GetValue<Guid>() == readerId);
        Assert.Equal($"object:{martin}", me["key"]!.GetValue<string>());
        Assert.Equal(["MMu", "Mára"], me["aliases"]!.AsArray().Select(alias => alias!.GetValue<string>()));
        Assert.Equal("Modelsoft", me["org"]!.GetValue<string>());
        Assert.Equal("PM", me["role"]!.GetValue<string>());
        Assert.Equal($"object:{jana}", people[Named("Jana Nováková")]["key"]!.GetValue<string>());
        Assert.Empty(lexicon["youTrackProjects"]!.AsArray());

        // A change gives a new version.
        var statements = AtlasServerTiming.Statements(string.Join(",", first.Headers.GetValues("Server-Timing")));
        await site.ExecuteAsync("""UPDATE "ContentResource" SET "Data"=jsonb_set("Data",'{aliases}','["MMu","Mára","Martas"]') WHERE "Id"=@martin""", ("martin", martin));
        await PageAsync(admin, space, "Druhá stránka", publish: true);
        var changed = await reader.GetAsync(path);
        var next = await AtlasHttpSite.Json(changed, HttpStatusCode.OK);
        Assert.NotEqual(lexicon["version"]!.GetValue<string>(), next["version"]!.GetValue<string>());
        Assert.Equal(2, next["pages"]!.AsArray().Count);
        Assert.Equal(statements, AtlasServerTiming.Statements(string.Join(",", changed.Headers.GetValues("Server-Timing"))));

        // The space's owner sees its own draft; a space the reader cannot reach is 404.
        var adminView = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.Contains("Koncept správce", adminView["pages"]!.AsArray().Select(page => page!["title"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/spaces/{hidden}/lexicon")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task A_list_over_its_cap_is_cut_and_says_so()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_lexicon_cap_") /* own database: inserts Cap+1 wiki contacts, and every lexicon of the installation lists the wiki's contacts up to that whole-wiki cap */;
        using var admin = site.Client();
        var adminId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<Guid>();
        var space = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        await site.ExecuteAsync("""
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data")
            SELECT gen_random_uuid(),'atlas.contact','wiki',NULL,NULL,1,now(),@admin,now(),'Kontakt ' || n, jsonb_build_object('name','Kontakt ' || n)
            FROM generate_series(1, @count) n;
            """, ("admin", adminId), ("count", SpaceLexiconHandler.Cap + 1));

        var lexicon = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{space}/lexicon"), HttpStatusCode.OK);
        Assert.True(lexicon["truncated"]!.GetValue<bool>());
        Assert.Equal(SpaceLexiconHandler.Cap, lexicon["people"]!.AsArray().Count(person => person!["contactId"] is JsonValue));
    }

    private static async Task<Guid> PageAsync(HttpClient admin, Guid space, string title, bool publish)
    {
        var id = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId = space, parentId = (Guid?)null, title, contentJson = Rich, markdown = "Text" }), HttpStatusCode.OK))["id"]!.GetValue<Guid>();
        if (publish)
        {
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{id}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        }
        return id;
    }

    /// <summary>A builtin account, activated and signed in on its own client (as AccessHttpTests does).</summary>
    private static async Task<(HttpClient Client, Guid Id)> PersonAsync(AtlasHttpSite site, HttpClient admin, string username, string displayName)
    {
        var login = TestLogin.For(username);
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = login, displayName, isWikiAdmin = false }), HttpStatusCode.Created);
        var client = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
            new { token = created["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password })).StatusCode);
        return (client, created["user"]!["id"]!.GetValue<Guid>());
    }
}
