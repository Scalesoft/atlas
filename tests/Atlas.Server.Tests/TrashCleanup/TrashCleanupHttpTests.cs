using System.Net;
using Atlas.Server.Features.TrashCleanup;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.TrashCleanup;

/// <summary>Cleanup of orphaned items (unified trash spec §4b): who sets which rule, the wiki's rule for all locking the spaces', and a
/// cleanup that purges what lay in the trash for its days, moves an unused object to the trash first and keeps an object used where its
/// admin cannot see; the daily run acts as the admin who switched it on.</summary>
public sealed class TrashCleanupHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Space_admins_set_their_rule_until_the_wiki_decides_for_all()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_trash_cleanup_rights_") /* own database: saves the wiki-wide cleanup rule (one row of the installation) that locks the rule of every space */;
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await SpaceAsync(admin, "Provoz");
        var (spaceAdmin, _) = await MemberAsync(site, admin, spaceId, "spravce", "Admin");
        var (writer, _) = await MemberAsync(site, admin, spaceId, "pisar", "Writer");
        using var _1 = spaceAdmin;
        using var _2 = writer;
        var path = $"/api/spaces/{spaceId}/trash-cleanup";

        var initial = await AtlasHttpSite.Json(await spaceAdmin.GetAsync(path), HttpStatusCode.OK);
        Assert.Equal((false, 60, false, false, 60), (initial["automatic"]!.GetValue<bool>(), initial["days"]!.GetValue<int>(),
            initial["decidedByWiki"]!.GetValue<bool>(), initial["effectiveAutomatic"]!.GetValue<bool>(), initial["effectiveDays"]!.GetValue<int>()));
        Assert.Null(initial["version"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, path, new { automatic = true, days = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"{path}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Put, path, new { automatic = true, days = 3 })).StatusCode);

        var saved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Put, path, new { automatic = true, days = 30 }), HttpStatusCode.OK);
        Assert.Equal((true, 30), (saved["effectiveAutomatic"]!.GetValue<bool>(), saved["effectiveDays"]!.GetValue<int>()));
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Put, path, new { automatic = false, days = 30 })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await spaceAdmin.GetAsync("/api/admin/trash-cleanup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Put, "/api/admin/trash-cleanup",
            new { scope = "wiki", automatic = false, days = 60 })).StatusCode);
        Assert.Equal("space", (await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/trash-cleanup"), HttpStatusCode.OK))["scope"]!.GetValue<string>());
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/trash-cleanup",
            new { scope = "wiki", automatic = false, days = 90 }), HttpStatusCode.OK);

        var decided = await AtlasHttpSite.Json(await spaceAdmin.GetAsync(path), HttpStatusCode.OK);
        Assert.Equal((true, 30, true, false, 90), (decided["automatic"]!.GetValue<bool>(), decided["days"]!.GetValue<int>(),
            decided["decidedByWiki"]!.GetValue<bool>(), decided["effectiveAutomatic"]!.GetValue<bool>(), decided["effectiveDays"]!.GetValue<int>()));
        var refused = await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Put, path,
            new { automatic = false, days = 30, expectedVersion = saved["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("decided_by_wiki", await refused.Content.ReadAsStringAsync());
    }

    [PostgreSqlFact]
    public async Task A_cleanup_purges_the_old_trash_and_moves_unused_objects_to_the_trash_first()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_trash_cleanup_run_") /* own database: runs the daily cleanup (TrashCleanupRunner.RunDueAsync), a job over every space and the wiki rule of the installation, and asserts its total */;
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await SpaceAsync(admin, "Provoz");
        var archiveId = await SpaceAsync(admin, "Archiv");
        var (spaceAdmin, spaceAdminId) = await MemberAsync(site, admin, spaceId, "spravce", "Admin");
        using var _ = spaceAdmin;

        // Published: a space admin sees and purges only published pages and their own drafts in the trash (ListPageTrashHandler).
        var page = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Stará", contentJson = Rich, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        var published = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page}/publish", new { expectedVersion = 1 }),
            HttpStatusCode.OK);
        var tree = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/tree"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page}/trash",
            new { includeSubtree = false, expectedTreeVersion = tree["version"]!.GetValue<long>(), expectedVersion = published["version"]!.GetValue<int>() }),
            HttpStatusCode.OK);
        var oldTrashed = await ContactAsync(admin, spaceId, "Bývalý");
        var youngTrashed = await ContactAsync(admin, spaceId, "Čerstvý");
        foreach (var id in new[] { oldTrashed, youngTrashed })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, $"/api/objects/{id}", null)).StatusCode);
        }
        var unused = await ContactAsync(admin, spaceId, "Nikde");
        // Used only in a space the space admin has no role in: still used.
        var usedElsewhere = await ContactAsync(admin, spaceId, "Jinde");
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{archiveId}/tree/references", new { targetId = usedElsewhere }),
            HttpStatusCode.Created);
        // Named only by an unpublished draft in that space: no relation source reads a draft, the content still keeps it.
        var drafted = await ContactAsync(admin, spaceId, "Koncept");
        var draftJson = $$$"""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Kontakt {{{drafted}}}"}]}]}}""";
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId = archiveId, parentId = (string?)null, title = "Rozpracované", contentJson = draftJson, markdown = $"Kontakt {drafted}" }), HttpStatusCode.OK);
        await site.ExecuteAsync("""UPDATE "Page" SET "TrashedAt" = now() - interval '90 days' WHERE "Id" = @id""", ("id", Guid.Parse(page)));
        // Bare SQL writes the page's envelope itself (since 3.6 ContentRowEffects mirrors only NHibernate's writes of the page).
        await site.ExecuteAsync("""UPDATE "ContentResource" SET "TrashedAt" = now() - interval '90 days' WHERE "Id" = ANY(@ids)""",
            ("ids", new[] { Guid.Parse(page), Guid.Parse(oldTrashed) }));
        await site.ExecuteAsync("""UPDATE "ContentResource" SET "UpdatedAt" = now() - interval '90 days' WHERE "Id" = ANY(@ids)""",
            ("ids", new[] { Guid.Parse(unused), Guid.Parse(usedElsewhere), Guid.Parse(drafted) }));

        var path = $"/api/spaces/{spaceId}/trash-cleanup";
        var counts = (await AtlasHttpSite.Json(await spaceAdmin.GetAsync(path), HttpStatusCode.OK))["counts"]!;
        Assert.Equal((3, 2, 1), (counts["trashed"]!.GetValue<int>(), counts["trashedDue"]!.GetValue<int>(), counts["unusedDue"]!.GetValue<int>()));

        var result = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Post, $"{path}/run", null), HttpStatusCode.OK);
        Assert.Equal((2, 1, 0), (result["purged"]!.GetValue<int>(), result["trashed"]!.GetValue<int>(), result["skipped"]!.GetValue<int>()));
        Assert.NotNull(await site.ScalarAsync("""SELECT "PurgedAt" FROM "Page" WHERE "Id" = @id""", ("id", Guid.Parse(page))));
        Assert.Equal(0L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = @id""", ("id", Guid.Parse(oldTrashed))));
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = @id AND "TrashedAt" IS NOT NULL""", ("id", Guid.Parse(youngTrashed))));
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = @id AND "TrashedAt" IS NOT NULL""", ("id", Guid.Parse(unused))));
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = @id AND "TrashedAt" IS NULL""", ("id", Guid.Parse(usedElsewhere))));
        Assert.Equal(2L, await site.ScalarAsync("""SELECT count(*) FROM "ObjectPurgeEvent" WHERE "PurgedById" = @actor""", ("actor", spaceAdminId)));

        // The daily run: only an automatic rule, as the admin who switched it on; the unused object leaves the trash for good a cleanup later.
        var runner = site.Services.GetRequiredService<TrashCleanupRunner>();
        Assert.Equal(0, await runner.RunDueAsync(DateTime.UtcNow.AddDays(61), CancellationToken.None));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(spaceAdmin, HttpMethod.Put, path, new { automatic = true, days = 60 }), HttpStatusCode.OK);
        Assert.Equal(2, await runner.RunDueAsync(DateTime.UtcNow.AddDays(61), CancellationToken.None));
        Assert.Equal(0L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = ANY(@ids)""",
            ("ids", new[] { Guid.Parse(unused), Guid.Parse(youngTrashed) })));
        Assert.Equal(4L, await site.ScalarAsync("""SELECT count(*) FROM "ObjectPurgeEvent" WHERE "PurgedById" = @actor""", ("actor", spaceAdminId)));
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = ANY(@ids) AND "TrashedAt" IS NULL""",
            ("ids", new[] { Guid.Parse(usedElsewhere) })));
        Assert.Equal(2L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = ANY(@ids) AND "TrashedAt" IS NULL""",
            ("ids", new[] { Guid.Parse(usedElsewhere), Guid.Parse(drafted) })));

        // Demoted to Writer, the admin who switched it on no longer stands behind the rule: the daily run leaves the space alone.
        var late = await ContactAsync(admin, spaceId, "Pozdě");
        await site.ExecuteAsync("""UPDATE "ContentResource" SET "UpdatedAt" = now() - interval '90 days' WHERE "Id" = @id""", ("id", Guid.Parse(late)));
        var grants = (await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/access"), HttpStatusCode.OK))["grants"]!.AsArray();
        var grantVersion = grants.Single(grant => grant!["userId"]?.GetValue<string>() == spaceAdminId.ToString())!["version"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = spaceAdminId, role = "Writer", expectedVersion = grantVersion }, await AtlasHttpSite.ReauthAsync(admin, site.Password))).StatusCode);
        Assert.Equal(0, await runner.RunDueAsync(DateTime.UtcNow.AddDays(61), CancellationToken.None));
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" = @id AND "TrashedAt" IS NULL""", ("id", Guid.Parse(late))));
    }

    private static Task<string> SpaceAsync(HttpClient admin, string name) => AtlasHttpSite.OwnSpaceAsync(admin, name);

    private static async Task<string> ContactAsync(HttpClient admin, string spaceId, string name)
        => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "space", spaceId, data = new { name } }), HttpStatusCode.Created))["id"]!.GetValue<string>();

    private static async Task<(HttpClient Client, Guid Id)> MemberAsync(AtlasHttpSite site, HttpClient admin, string spaceId, string username, string role)
    {
        var login = TestLogin.For(username);
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = login, displayName = username, isWikiAdmin = false }), HttpStatusCode.Created);
        var client = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<string>(), role }, await AtlasHttpSite.ReauthAsync(admin, site.Password))).StatusCode);
        return (client, invitation["user"]!["id"]!.GetValue<Guid>());
    }
}
