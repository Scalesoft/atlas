using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

/// <summary>The space trash over HTTP: confirmation by name, every
/// space route and object answering 404 while the space is in the trash (the wiki admin included), and the restore bringing back exactly the
/// pages trashed with the space.</summary>
public sealed class SpaceTrashHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task A_trashed_space_is_missing_everywhere_and_its_restore_brings_back_its_pages()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_trash_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var name = TestLogin.For("Provoz");
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name, groups = Array.Empty<object>() }), HttpStatusCode.OK);
        var spaceId = created["id"]!.GetValue<string>();
        // The list as a member of this space: the administrator's holds only the first 200 spaces of the shared site.
        using var member = await SpacesHttpTests.MemberAsync(site, admin, spaceId, "Reader");
        await SpacesHttpTests.ListedAsync(member, spaceId);
        var parent = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Runbooky", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var parentId = parent["id"]!.GetValue<string>();
        var child = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId, title = "Zálohy", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var earlier = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Stará", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var earlierId = earlier["id"]!.GetValue<string>();
        var tree = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/tree"), HttpStatusCode.OK);
        // A page trashed before the space keeps its own batch and stays in the page trash after the restore.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{earlierId}/trash",
            new { includeSubtree = false, expectedTreeVersion = tree["version"]!.GetValue<long>(), expectedVersion = 1 }), HttpStatusCode.OK);

        // An object of the space beyond the tree (a contact), placed as a reference in another space's tree.
        var contactName = TestLogin.For("Jana");
        var contactId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "space", spaceId, data = new { name = contactName } }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        // The type's list is paged over every test's contacts: read it by this contact's unique name.
        var contacts = $"/api/objects?type=atlas.contact&name={Uri.EscapeDataString(contactName)}";
        Assert.Contains(contactId, (await AtlasHttpSite.Json(await admin.GetAsync(contacts), HttpStatusCode.OK)).ToJsonString());
        var otherId = await AtlasHttpSite.OwnSpaceAsync(admin, "Archiv");
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{otherId}/tree/references", new { targetId = contactId }),
            HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name = name.ToLowerInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name = $" {name} " })).StatusCode);

        var spaces = await AtlasHttpSite.Json(await member.GetAsync("/api/spaces"), HttpStatusCode.OK);
        Assert.DoesNotContain(spaces.AsArray(), space => space!["id"]!.GetValue<string>() == spaceId);
        var trash = (await AtlasHttpSite.Json(await admin.GetAsync("/api/space-trash"), HttpStatusCode.OK)).AsArray();
        var item = Assert.Single(trash, x => x!["id"]?.GetValue<string>() == spaceId);
        Assert.Equal((name, 2), (item!["name"]!.GetValue<string>(), item["pageCount"]!.GetValue<int>()));

        // The matrix: every route of the space and of its pages answers as for a space that never existed.
        string[] reads =
        [
            "tree", "trash", "pages", "settings", "access", "ai-settings", "page-templates", "drafts", "duplicates", "external-sharing",
            "share-links", "search-gaps", "content-validation", "export", "cover",
        ];
        foreach (var route in reads)
        {
            Assert.True(HttpStatusCode.NotFound == (await admin.GetAsync($"/api/spaces/{spaceId}/{route}")).StatusCode, route);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/pages/{parentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Nová", contentJson = Rich, markdown = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/me/spaces/{spaceId}/pin", new { pinned = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/objects/{contactId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/objects/{contactId}/relations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/objects/{contactId}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{contactId}/comments", new { body = "Ještě" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{contactId}/changes",
            new { changeId = Guid.NewGuid(), expectedVersion = 1, payload = new { fields = new { name = "Jana N." } } })).StatusCode);
        Assert.DoesNotContain(contactId, (await AtlasHttpSite.Json(await admin.GetAsync(contacts), HttpStatusCode.OK)).ToJsonString());
        Assert.Equal(0, (await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects?spaceId={spaceId}"), HttpStatusCode.OK))["total"]!.GetValue<int>());
        var reference = Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{otherId}/tree"), HttpStatusCode.OK))["items"]!.AsArray())!;
        Assert.Equal((false, null), (reference["readable"]!.GetValue<bool>(), reference["title"]?.GetValue<string>()));
        var found = await AtlasHttpSite.Json(await admin.GetAsync($"/api/search?q={name}"), HttpStatusCode.OK);
        Assert.DoesNotContain(spaceId, found.ToJsonString());

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/restore")).StatusCode);
        Assert.DoesNotContain((await AtlasHttpSite.Json(await admin.GetAsync("/api/space-trash"), HttpStatusCode.OK)).AsArray(), x => x!["id"]?.GetValue<string>() == spaceId);
        var restored = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/tree"), HttpStatusCode.OK);
        var items = restored["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, x => x!["id"]!.GetValue<string>() == child["id"]!.GetValue<string>() && x["parentId"]!.GetValue<string>() == parentId);
        var pageTrash = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/trash"), HttpStatusCode.OK);
        Assert.Equal(earlierId, Assert.Single(pageTrash["items"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/objects/{contactId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/restore")).StatusCode);
    }
    [PostgreSqlFact]
    public async Task Whoever_is_no_wiki_admin_gets_one_answer_for_every_space_id()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_trash_probe_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        async Task<string> SpaceAsync(string name) => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces",
            new { name, groups = Array.Empty<object>() }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        var live = await SpaceAsync(TestLogin.For("Živý"));
        var trashedName = TestLogin.For("Odložený");
        var trashed = await SpaceAsync(trashedName);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{trashed}/trash", new { name = trashedName })).StatusCode);
        var curiousLogin = TestLogin.For("zvedavec");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = curiousLogin, displayName = "Zvědavec" }), HttpStatusCode.Created);
        using var probe = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(probe, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(probe, HttpMethod.Post, "/api/auth/login", new { username = curiousLogin, password = site.Password }), HttpStatusCode.OK);

        foreach (var id in new[] { live, trashed, Guid.NewGuid().ToString() })
        {
            Assert.True(HttpStatusCode.Forbidden == (await AtlasHttpSite.SendAsync(probe, HttpMethod.Post, $"/api/spaces/{id}/trash", new { name = "x" })).StatusCode, id);
            Assert.True(HttpStatusCode.Forbidden == (await AtlasHttpSite.SendAsync(probe, HttpMethod.Post, $"/api/spaces/{id}/restore")).StatusCode, id);
            Assert.True(HttpStatusCode.Forbidden == (await AtlasHttpSite.SendAsync(probe, HttpMethod.Post, $"/api/spaces/{id}/purge", new { name = trashedName })).StatusCode, id);
        }
        // The permanent delete is the web's: no personal API route, and the wiki admin's purge answers 204, then 404.
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/v1/spaces/{trashed}/purge", new { name = trashedName })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{trashed}/purge", new { name = trashedName })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{trashed}/purge", new { name = trashedName })).StatusCode);
    }

    /// <summary>A trashed space grants nothing, not even the rights beyond it: a Writer only there no longer writes the wiki contact directory.</summary>
    [PostgreSqlFact]
    public async Task A_writer_only_in_a_trashed_space_no_longer_writes_wiki_contacts()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_trash_wiki_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var project = TestLogin.For("Projekt");
        var spaceId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name = project, groups = Array.Empty<object>() }),
            HttpStatusCode.OK))["id"]!.GetValue<string>();
        var login = TestLogin.For("externista");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = login, displayName = "Externista", isWikiAdmin = false }), HttpStatusCode.Created);
        using var writer = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<string>(), role = "Writer" })).StatusCode);
        var contactId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "wiki", data = new { name = "Jana" } }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        async Task<bool> CanCreateAsync() =>
            (await AtlasHttpSite.Json(await writer.GetAsync("/api/objects?type=atlas.contact&scope=wiki"), HttpStatusCode.OK))["canCreate"]!.GetValue<bool>();
        Assert.True(await CanCreateAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name = project })).StatusCode);

        Assert.False(await CanCreateAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "wiki", data = new { name = "Petr" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/objects/{contactId}/changes",
            new { changeId = Guid.NewGuid(), expectedVersion = 1, payload = new { fields = new { name = "Jana N." } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Delete, $"/api/objects/{contactId}")).StatusCode);
    }

    /// <summary>A trashed space grants nothing in the one-space role either: its former admin no longer opens anyone's access card
    /// scoped to it, and gets it back with the restore.</summary>
    [PostgreSqlFact]
    public async Task An_admin_only_of_a_trashed_space_reads_no_access_card_until_the_restore()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_trash_card_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var project = TestLogin.For("Projekt");
        var spaceId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name = project, groups = Array.Empty<object>() }),
            HttpStatusCode.OK))["id"]!.GetValue<string>();
        var managerLogin = TestLogin.For("spravce");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = managerLogin, displayName = "Správce", isWikiAdmin = false }), HttpStatusCode.Created);
        var other = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = TestLogin.For("jana"), displayName = "Jana", isWikiAdmin = false }), HttpStatusCode.Created);
        using var manager = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(manager, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(manager, HttpMethod.Post, "/api/auth/login", new { username = managerLogin, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<string>(), role = "Admin" }, await AtlasHttpSite.ReauthAsync(admin, site.Password))).StatusCode);
        var card = $"/api/access/principals/user/{other["user"]!["id"]!.GetValue<string>()}?spaceId={spaceId}";
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(card)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name = project })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(card)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(card)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/restore")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(card)).StatusCode);
    }

    /// <summary>A trashed space grants no role on its pages either: its former Admin and Writer get from the page restore, for every tree
    /// version, the very answer of an account without a grant and of an id that never existed, so none tells whether the space is purged yet.</summary>
    [PostgreSqlFact]
    public async Task Former_members_of_a_trashed_space_restore_its_pages_as_strangers()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_trash_untrash_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var project = TestLogin.For("Projekt");
        var spaceId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name = project, groups = Array.Empty<object>() }),
            HttpStatusCode.OK))["id"]!.GetValue<string>();
        var pageId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Plán", contentJson = Rich, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        // Published, so the private draft rule does not hide it from the members already.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        async Task<HttpClient> MemberAsync(string username, string? role)
        {
            var login = TestLogin.For(username);
            var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
                new { username = login, displayName = username, isWikiAdmin = false }), HttpStatusCode.Created);
            var client = site.Client();
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
                new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password }), HttpStatusCode.OK);
            if (role is not null)
            {
                Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
                    new { userId = invitation["user"]!["id"]!.GetValue<string>(), role }, await AtlasHttpSite.ReauthAsync(admin, site.Password))).StatusCode);
            }
            return client;
        }
        using var former = await MemberAsync("spravce", "Admin");
        using var writer = await MemberAsync("pisar", "Writer");
        using var stranger = await MemberAsync("cizi", null);
        var version = (await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/tree"), HttpStatusCode.OK))["version"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name = project })).StatusCode);

        static async Task<string> AnswerAsync(HttpClient client, string id, long expectedTreeVersion)
        {
            var response = await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/pages/{id}/untrash",
                new { parentId = (string?)null, includeSubtree = true, expectedTreeVersion });
            return $"{(int)response.StatusCode} {System.Text.RegularExpressions.Regex.Replace(await response.Content.ReadAsStringAsync(), "\"traceId\":\"[^\"]*\"", "")}";
        }
        var missing = await AnswerAsync(stranger, Guid.NewGuid().ToString(), version);
        Assert.StartsWith("404 ", missing);
        for (var expected = 0L; expected <= version + 4; expected++)
        {
            foreach (var (name, client) in new[] { ("former admin", former), ("former writer", writer), ("stranger", stranger) })
            {
                Assert.True(missing == await AnswerAsync(client, pageId, expected), $"{name} at tree version {expected}");
            }
        }
    }

    /// <summary>The space trash and its restore lock, read and reset the pages of the space in a constant number of statements; only
    /// the row updates of each page are one statement each (NHibernate sends no batches): the page and, opened in the editor, its live
    /// state (CollaborationCheckpoints.ResetAllAsync).</summary>
    [PostgreSqlFact]
    public async Task The_space_trash_and_restore_cost_one_statement_per_page()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_trash_cost_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);

        async Task<(int Trash, int Restore)> StatementsAsync(string name, int pages)
        {
            name = TestLogin.For(name);
            var spaceId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name, groups = Array.Empty<object>() }),
                HttpStatusCode.OK))["id"]!.GetValue<string>();
            string? parentId = null;
            for (var index = 0; index < pages; index++)
            {
                var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                    new { spaceId, parentId = index % 2 == 0 ? null : parentId, title = $"Stránka {index}", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
                parentId = page["id"]!.GetValue<string>();
                await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{parentId}/collaboration"), HttpStatusCode.OK);
            }
            var trash = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/trash", new { name });
            Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
            var restore = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/restore");
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
            return (Statements(trash), Statements(restore));
        }

        var few = await StatementsAsync("Malý", 5);
        var many = await StatementsAsync("Velký", 30);
        Assert.True(many.Trash - few.Trash <= 2 * 25, $"trash: {few.Trash} SQL for 5 pages, {many.Trash} for 30");
        // + 2: a connection the pool opens for the request under a parallel run is counted too (Npgsql's CONNECT), once, not per page.
        Assert.True(many.Restore - few.Restore <= 25 + 2, $"restore: {few.Restore} SQL for 5 pages, {many.Restore} for 30");

        static int Statements(HttpResponseMessage response) =>
            Server.Platform.Hosting.AtlasServerTiming.Statements(string.Join(",", response.Headers.GetValues("Server-Timing")))!.Value;
    }
}
