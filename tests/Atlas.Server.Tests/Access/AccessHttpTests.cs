using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Access;

/// <summary>
/// The security centre's access layer (docs/access-model.md §2–3) over HTTP. The cards and lists must agree with the authorization
/// checks: every level asserted here is also checked against what the account can really do.
/// </summary>
public sealed class AccessHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
    private const string VaultFixture = "argon2d-aes-gzip.kdbx";

    /// <summary>A person's card: the effective level of every entry is what the checks give (a direct Reader grant does not lower
    /// a Writer group grant, an expired grant counts for nothing, an own-access page without a grant is excluded, a space Admin is
    /// never excluded, a page grant alone reaches a page), and the sources explain it. The card of oneself is the same.</summary>
    [PostgreSqlFact]
    public async Task Person_card_levels_match_the_authorization_checks()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_access_card_");
        using var admin = site.Client();
        var adminId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<Guid>();
        var spaceA = await FirstSpaceAsync(admin);
        var spaceB = await SpaceAsync(admin, "Beta");
        var spaceC = await SpaceAsync(admin, "Gama");
        var (jana, janaId) = await PersonAsync(site, admin, "jana");
        var provoz = TestLogin.For("Provoz");
        var group = await GroupAsync(site, admin, provoz, janaId);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceA}/access", new { userId = janaId, role = "Reader" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceA}/access", new { groupId = group, role = "Writer" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceB}/access", new { userId = janaId, role = "Admin" }, HttpStatusCode.NoContent,
            await AtlasHttpSite.ReauthAsync(admin, site.Password));
        await site.ExecuteAsync("""
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","StartAt","ExpiresAt","ActiveFrom")
            VALUES (gen_random_uuid(), @space, @user, 2, '2020-01-01T00:00:00Z', '2021-01-01T00:00:00Z', '2020-01-01T00:00:00Z')
            """, ("space", spaceC), ("user", janaId));
        var excluded = await PageAsync(admin, spaceA, "Jen pro vybrané");
        var adminOwn = await PageAsync(admin, spaceB, "Správa");
        var granted = await PageAsync(admin, spaceC, "Sdílená stránka");
        await site.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\" = true WHERE \"Id\" = @a OR \"Id\" = @b", ("a", excluded), ("b", adminOwn));
        await Send(admin, HttpMethod.Put, $"/api/pages/{granted}/access/grants", new { userId = janaId, role = "Writer" }, HttpStatusCode.NoContent);

        var card = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/principals/user/{janaId}"), HttpStatusCode.OK);
        Assert.Equal("wiki", card["scope"]!.GetValue<string>());
        Assert.Equal(await site.ScalarAsync("SELECT \"Username\" FROM \"UserAccount\" WHERE \"Id\" = @id", ("id", janaId)), card["principal"]!["detail"]!.GetValue<string>());
        Assert.Equal(provoz, card["principal"]!["groups"]![0]!["name"]!.GetValue<string>());
        var a = Entry(card, "space", spaceA);
        Assert.Equal("write", Level(a));
        Assert.Equal(("group", provoz, "write", true), Source(a, 0));
        Assert.Equal(("direct", "Přímo přidělený přístup", "read", true), Source(a, 1));
        Assert.Equal("manage", Level(Entry(card, "space", spaceB)));
        var c = Entry(card, "space", spaceC);
        Assert.Null(Level(c));
        Assert.False(c["sources"]![0]!["effective"]!.GetValue<bool>());
        Assert.Null(Level(Entry(card, "page", excluded)));
        Assert.DoesNotContain(card["entries"]!.AsArray(), e => e!["resourceId"]!.GetValue<string>() == adminOwn.ToString());
        Assert.Equal("write", Level(Entry(card, "page", granted)));

        // The checks agree: the space roles of the list, and the pages the account can open.
        var spaces = await AtlasHttpSite.Json(await jana.GetAsync("/api/spaces"), HttpStatusCode.OK);
        Assert.Equal("Writer", ListedSpace(spaces, spaceA)["role"]!.GetValue<string>());
        Assert.Equal("Admin", ListedSpace(spaces, spaceB)["role"]!.GetValue<string>());
        Assert.True(ListedSpace(spaces, spaceC)["itemAccessOnly"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await jana.GetAsync($"/api/pages/{excluded}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jana.GetAsync($"/api/pages/{adminOwn}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jana.GetAsync($"/api/pages/{granted}")).StatusCode);

        var self = await AtlasHttpSite.Json(await jana.GetAsync("/api/access/me"), HttpStatusCode.OK);
        Assert.Equal("self", self["scope"]!.GetValue<string>());
        Assert.False(self["canManage"]!.GetValue<bool>());
        Assert.Equal(card["entries"]!.ToJsonString(), self["entries"]!.ToJsonString());

        // A WikiAdmin: the wiki entry (set directly, not by the OIDC mapping) and every space through it.
        var mine = await AtlasHttpSite.Json(await admin.GetAsync("/api/access/me"), HttpStatusCode.OK);
        var wiki = Entry(mine, "wiki", "wiki");
        Assert.Equal(("direct", "Správce wiki", "manage", true), Source(wiki, 0));
        Assert.Equal("manage", Level(Entry(mine, "space", spaceC)));
        Assert.Contains(Entry(mine, "space", spaceC)["sources"]!.AsArray(), s => s!["via"]!.GetValue<string>() == "automatic");
        Assert.Equal(adminId, mine["principal"]!["id"]!.GetValue<Guid>());

        var kinds = await AtlasHttpSite.Json(await admin.GetAsync("/api/access/kinds"), HttpStatusCode.OK);
        Assert.Equal(["wiki", "space", "page", "token", "vault", "secret", "item"], kinds.AsArray().Select(k => k!["id"]!.GetValue<string>()).ToArray());
        Assert.True(kinds.AsArray().Single(k => k!["id"]!.GetValue<string>() == "space")!["grantable"]!.GetValue<bool>());
        Assert.False(kinds.AsArray().Single(k => k!["id"]!.GetValue<string>() == "wiki")!["grantable"]!.GetValue<bool>());
    }

    /// <summary>Who has access to a space and a page: direct lists only the grants set there, effective adds the members of granted
    /// groups; WikiAdmins and space Admins are the locked automatic rows; the space names its exceptions. Managers only.</summary>
    [PostgreSqlFact]
    public async Task Resource_view_lists_direct_or_effective_access_with_automatic_rows_and_exceptions()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_access_resource_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var space = await FirstSpaceAsync(admin);
        var (jana, janaId) = await PersonAsync(site, admin, "jana");
        var (_, petrId) = await PersonAsync(site, admin, "petr");
        var provoz = TestLogin.For("Provoz");
        var group = await GroupAsync(site, admin, provoz, janaId);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new { groupId = group, role = "Writer" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new { userId = petrId, role = "Reader" }, HttpStatusCode.NoContent);
        var own = await PageAsync(admin, space, "Vlastní přístup");
        var shared = await PageAsync(admin, space, "Sdílená");
        await site.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\" = true WHERE \"Id\" = @page", ("page", own));
        await Send(admin, HttpMethod.Put, $"/api/pages/{shared}/access/grants", new { userId = petrId, role = "Writer" }, HttpStatusCode.NoContent);

        var direct = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/space/{space}?view=direct"), HttpStatusCode.OK);
        Assert.True(direct["canManage"]!.GetValue<bool>());
        // Every test of the shared installation has an administrator of its own, so the count is at least this test's.
        Assert.Equal("Správci wiki", direct["automatic"]![0]!["label"]!.GetValue<string>());
        Assert.True(direct["automatic"]![0]!["count"]!.GetValue<int>() >= 1);
        Assert.Null(Subject(direct, janaId));
        Assert.Equal("write", Subject(direct, group)!["level"]!.GetValue<string>());
        Assert.Equal("read", Subject(direct, petrId)!["level"]!.GetValue<string>());
        var effective = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/space/{space}"), HttpStatusCode.OK);
        var viaGroup = Subject(effective, janaId)!;
        Assert.Equal("write", viaGroup["level"]!.GetValue<string>());
        Assert.Equal(("group", provoz), (viaGroup["sources"]![0]!["via"]!.GetValue<string>(), viaGroup["sources"]![0]!["label"]!.GetValue<string>()));
        var exceptions = effective["exceptions"]!.AsArray();
        Assert.Equal(("own", 0), Exception(exceptions, own));
        Assert.Equal(("grants", 1), Exception(exceptions, shared));

        var pageDirect = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/page/{shared}?view=direct"), HttpStatusCode.OK);
        Assert.Equal(2, pageDirect["automatic"]!.AsArray().Count);
        Assert.Equal(petrId, Assert.Single(pageDirect["entries"]!.AsArray())!["subject"]!["id"]!.GetValue<Guid>());
        Assert.Equal("write", Subject(pageDirect, petrId)!["level"]!.GetValue<string>());
        var pageEffective = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/page/{shared}?view=effective"), HttpStatusCode.OK);
        Assert.Equal("write", Subject(pageEffective, janaId)!["level"]!.GetValue<string>());
        Assert.Equal("write", Subject(pageEffective, group)!["level"]!.GetValue<string>());
        Assert.Equal(2, Subject(pageEffective, petrId)!["sources"]!.AsArray().Count);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/access/resources/space/{space}?view=all")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/access/resources/token/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jana.GetAsync($"/api/access/resources/space/{space}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jana.GetAsync($"/api/access/resources/page/{shared}")).StatusCode);
    }

    /// <summary>The space list stops at 200 entries; q (name or username, case-insensitive, LIKE wildcards literal) narrows it before
    /// that cap, so a member beyond the first 200 is found with the same level and reasons, a deactivated member still is not, groups
    /// match by name, and direct keeps to the grants set here.</summary>
    [PostgreSqlFact]
    public async Task Space_resource_view_finds_anyone_by_name_beyond_the_first_two_hundred()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_access_search_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var space = await FirstSpaceAsync(admin);
        var vsichni = TestLogin.For("Všichni");
        var group = await GroupAsync(site, admin, vsichni);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new { groupId = group, role = "Reader" }, HttpStatusCode.NoContent);
        // Logins are unique in the whole installation: this test's end with a tag of its own.
        var tag = Guid.NewGuid().ToString("N")[..8];
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
            SELECT gen_random_uuid(), 'clen-' || lpad(i::text, 3, '0') || '-' || @tag, 'Člen ' || lpad(i::text, 3, '0'), false, true, 'stamp' FROM generate_series(1, 260) i
            UNION ALL VALUES (gen_random_uuid(), 'zofie_1-' || @tag, 'Žofie 100% hotovo', false, true, 'stamp'),
                (gen_random_uuid(), 'zofiex1-' || @tag, 'Žofie 1000 hotovo', false, true, 'stamp'),
                (gen_random_uuid(), 'zofie-off-' || @tag, 'Žofie neaktivní', false, false, 'stamp');
            INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom")
            SELECT gen_random_uuid(), @group, u."Id", statement_timestamp() FROM "UserAccount" u WHERE u."Username" LIKE ('%-' || @tag);
            INSERT INTO "UserAccountRevision"("Id","UserId","Username","DisplayName","IsActive","IsWikiAdmin","ActiveFrom")
            SELECT gen_random_uuid(), u."Id", u."Username", u."DisplayName", u."IsActive", false, now() FROM "UserAccount" u
            WHERE u."Username" LIKE ('%-' || @tag)
              AND NOT EXISTS (SELECT 1 FROM "UserAccountRevision" r WHERE r."UserId" = u."Id" AND r."ActiveTo" IS NULL);
            """, ("group", group), ("tag", tag));
        var direct = (Guid)(await site.ScalarAsync("SELECT \"Id\" FROM \"UserAccount\" WHERE \"Username\" = 'clen-250-' || @tag", ("tag", tag)))!;
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new { userId = direct, role = "Writer" }, HttpStatusCode.NoContent);
        async Task<JsonNode> List(string query) => await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/space/{space}{query}"), HttpStatusCode.OK);
        static string[] Names(JsonNode list) => list["entries"]!.AsArray().Select(e => e!["subject"]!["name"]!.GetValue<string>()).ToArray();

        var all = await List("");
        Assert.Equal(200, all["entries"]!.AsArray().Count);
        Assert.True(all["hasMore"]!.GetValue<bool>());
        Assert.DoesNotContain("Žofie 100% hotovo", Names(all));

        var percent = await List("?q=" + Uri.EscapeDataString("100%"));
        Assert.Equal(["Žofie 100% hotovo"], Names(percent));
        Assert.False(percent["hasMore"]!.GetValue<bool>());
        var found = percent["entries"]![0]!;
        Assert.Equal(("read", true), (found["level"]!.GetValue<string>(), found["subject"]!["isActive"]!.GetValue<bool>()));
        Assert.Equal(("group", vsichni, "read", true), Source(found, 0));
        Assert.Equal(["Žofie 100% hotovo"], Names(await List("?q=e_1")));
        Assert.Equal(["Žofie 100% hotovo", "Žofie 1000 hotovo"], Names(await List("?q=%C5%BDOFIE")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([vsichni], Names(await List("?q=" + Uri.EscapeDataString(vsichni))));

        var both = (await List("?q=clen-250"))["entries"]![0]!;
        Assert.Equal("write", both["level"]!.GetValue<string>());
        Assert.Equal([("direct", "Přímo přidělený přístup", "write", true), ("group", vsichni, "read", true)], [Source(both, 0), Source(both, 1)]);
        Assert.Equal(["Člen 250"], Names(await List("?view=direct&q=clen-25")));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/access/resources/space/{space}?q={new string('x', 201)}")).StatusCode);
    }

    /// <summary>A space Admin sees a person's card only for its own space, without the wiki-wide facts; the candidate directory needs
    /// a term, lists at most 20 active accounts and only to someone who manages something; the space access list carries no directory.</summary>
    [PostgreSqlFact]
    public async Task Space_admin_scope_and_candidates_never_disclose_the_directory()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_access_scope_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceA = await FirstSpaceAsync(admin);
        var spaceB = await SpaceAsync(admin, "Beta");
        var (manager, managerId) = await PersonAsync(site, admin, "spravce");
        var (jana, janaId) = await PersonAsync(site, admin, "jana");
        var provoz = TestLogin.For("Provoz");
        var group = await GroupAsync(site, admin, provoz, janaId);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceA}/access", new { userId = managerId, role = "Admin" }, HttpStatusCode.NoContent,
            await AtlasHttpSite.ReauthAsync(admin, site.Password));
        await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceA}/access", new { userId = janaId, role = "Reader" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceB}/access", new { userId = janaId, role = "Writer" }, HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/access/principals/user/{janaId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/access/principals/user/{janaId}?spaceId={spaceB}")).StatusCode);
        var card = await AtlasHttpSite.Json(await manager.GetAsync($"/api/access/principals/user/{janaId}?spaceId={spaceA}"), HttpStatusCode.OK);
        Assert.Equal("space", card["scope"]!.GetValue<string>());
        Assert.True(card["canManage"]!.GetValue<bool>());
        Assert.All(card["entries"]!.AsArray(), e => Assert.Equal(spaceA, e!["spaceId"]!.GetValue<Guid>()));
        Assert.Equal("read", Level(Entry(card, "space", spaceA)));
        Assert.Null(card["principal"]!["groups"]);
        Assert.Null(card["principal"]!["lastSignInAt"]);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/access/principals/group/{group}?spaceId={spaceA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jana.GetAsync($"/api/access/principals/user/{managerId}?spaceId={spaceB}")).StatusCode);

        // Logins are unique in the whole installation: this test's carry a tag of its own.
        var kandidat = TestLogin.For("kandidat");
        for (var index = 0; index < 25; index++)
        {
            await site.ExecuteAsync("""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
                VALUES (gen_random_uuid(), @name, @name, false, true, 'stamp')
                """, ("name", $"{kandidat}-{index:00}"));
        }
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
            VALUES (gen_random_uuid(), @name, @name, false, false, 'stamp')
            """, ("name", $"{kandidat}-off"));
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/access/candidates?kind=user")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/access/candidates?kind=role&q=k")).StatusCode);
        Assert.Equal(20, (await AtlasHttpSite.Json(await manager.GetAsync($"/api/access/candidates?kind=user&q={kandidat}"), HttpStatusCode.OK)).AsArray().Count);
        Assert.Empty((await AtlasHttpSite.Json(await manager.GetAsync($"/api/access/candidates?kind=user&q={kandidat}-off"), HttpStatusCode.OK)).AsArray());
        Assert.Equal(provoz, (await AtlasHttpSite.Json(await manager.GetAsync("/api/access/candidates?kind=group&q=" + Uri.EscapeDataString(provoz)), HttpStatusCode.OK))[0]!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await jana.GetAsync("/api/access/candidates?kind=user&q=kandidat")).StatusCode);

        var access = await AtlasHttpSite.Json(await manager.GetAsync($"/api/spaces/{spaceA}/access"), HttpStatusCode.OK);
        Assert.Null(access["users"]);
        Assert.Null(access["groups"]);
        Assert.Equal(3, access["grants"]!.AsArray().Count);
        var kinds = await AtlasHttpSite.Json(await jana.GetAsync("/api/access/kinds"), HttpStatusCode.OK);
        Assert.All(kinds.AsArray(), k => Assert.False(k!["grantable"]!.GetValue<bool>()));
        Assert.Equal([spaceA.ToString()], (await AtlasHttpSite.Json(await manager.GetAsync("/api/access/resources?kind=space"), HttpStatusCode.OK))
            .AsArray().Select(r => r!["id"]!.GetValue<string>()).ToArray());
    }

    /// <summary>The generic grant endpoints write through each kind's handler: manage on a space or page, every vault grant and the
    /// WikiAdmin switch need a fresh sign-in (then the session window covers the rest), levels the kind does not use are refused,
    /// and CAS versions from the resource list revoke.</summary>
    [PostgreSqlFact]
    public async Task Generic_grants_write_through_the_kind_handlers_and_need_a_fresh_sign_in_for_manage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-access-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var site = await AtlasHttpSite.CreateAsync("atlas_access_write_", settings => settings["Vaults:RefreshSeconds"] = "0");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var space = await FirstSpaceAsync(admin);
        var (_, janaId) = await PersonAsync(site, admin, "jana");
        var page = await PageAsync(admin, space, "Provoz");
        var grants = $"/api/access/resources/space/{space}/grants";

        await Send(admin, HttpMethod.Put, grants, new { subjectKind = "user", subjectId = janaId, level = "read" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, grants, new { subjectKind = "user", subjectId = janaId, level = "discover" }, HttpStatusCode.BadRequest);
        await Send(admin, HttpMethod.Put, grants, new { subjectKind = "robot", subjectId = janaId, level = "read" }, HttpStatusCode.BadRequest);
        var version = Subject(await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/space/{space}?view=direct"), HttpStatusCode.OK),
            janaId)!["sources"]![0]!["version"]!.GetValue<string>();
        var refusals = new[]
        {
            await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, grants,
                new { subjectKind = "user", subjectId = janaId, level = "manage", expectedVersion = version }),
            await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/access/resources/page/{page}/grants",
                new { subjectKind = "user", subjectId = janaId, level = "manage" }),
            await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/admin/users/{janaId}",
                new { displayName = "jana", isActive = true, isWikiAdmin = true, expectedVersion = await UserVersionAsync(admin, janaId) })
        };
        foreach (var refused in refusals)
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("reauth_required", (await AtlasHttpSite.Json(refused, HttpStatusCode.Forbidden))["code"]!.GetValue<string>());
        }
        await Send(admin, HttpMethod.Put, grants, new { subjectKind = "user", subjectId = janaId, level = "manage", expectedVersion = version },
            HttpStatusCode.NoContent, await AtlasHttpSite.ReauthAsync(admin, site.Password));
        // Inside the session's window the next security changes need no proof.
        await Send(admin, HttpMethod.Put, $"/api/access/resources/page/{page}/grants", new { subjectKind = "user", subjectId = janaId, level = "manage" },
            HttpStatusCode.NoContent);
        var pageList = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/page/{page}?view=direct"), HttpStatusCode.OK);
        Assert.Equal("manage", Subject(pageList, janaId)!["level"]!.GetValue<string>());
        var spaceList = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/space/{space}?view=direct"), HttpStatusCode.OK);
        var current = Subject(spaceList, janaId)!["sources"]![0]!["version"]!.GetValue<string>();
        await Send(admin, HttpMethod.Delete, $"{grants}/user/{janaId}?version={Uri.EscapeDataString(version)}", null, HttpStatusCode.Conflict);
        await Send(admin, HttpMethod.Delete, $"{grants}/user/{janaId}?version={Uri.EscapeDataString(current)}", null, HttpStatusCode.NoContent);
        Assert.Null(Subject(await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/space/{space}?view=direct"), HttpStatusCode.OK), janaId));
        await Send(admin, HttpMethod.Put, $"/api/access/resources/token/{Guid.NewGuid()}/grants", new { subjectKind = "user", subjectId = janaId, level = "read" },
            HttpStatusCode.BadRequest);
        await Send(admin, HttpMethod.Put, $"/api/access/resources/nothing/{Guid.NewGuid()}/grants", new { subjectKind = "user", subjectId = janaId, level = "read" },
            HttpStatusCode.NotFound);

        var location = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/vault-locations",
            new { name = "Místní", kind = "local", root = folder }), HttpStatusCode.OK);
        File.WriteAllBytes(Path.Combine(folder, "provoz.kdbx"), Read(VaultFixture));
        var vault = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/vaults/attach", new
        {
            spaceId = space, locationId = location["id"]!.GetValue<string>(), name = "Provoz", fileName = "provoz.kdbx", password = Password(VaultFixture)
        }), HttpStatusCode.OK))["id"]!.GetValue<Guid>();
        var vaultGrants = $"/api/access/resources/vault/{vault}/grants";
        await Send(admin, HttpMethod.Put, vaultGrants, new { subjectKind = "user", subjectId = janaId, level = "write" }, HttpStatusCode.BadRequest);
        await Send(admin, HttpMethod.Put, vaultGrants, new { subjectKind = "user", subjectId = janaId, level = "read" }, HttpStatusCode.NoContent);
        var vaultList = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/resources/vault/{vault}?view=direct"), HttpStatusCode.OK);
        var person = Subject(vaultList, janaId)!;
        Assert.Equal("read", person["level"]!.GetValue<string>());
        var card = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/principals/user/{janaId}"), HttpStatusCode.OK);
        Assert.Equal(("read", "4 hesla"), (Level(Entry(card, "vault", vault)), Entry(card, "vault", vault)["note"]!.GetValue<string>()));
        await Send(admin, HttpMethod.Delete, $"{vaultGrants}/user/{janaId}?version={Uri.EscapeDataString(person["sources"]![0]!["version"]!.GetValue<string>())}",
            null, HttpStatusCode.NoContent);
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"ResourceGrant\" WHERE \"VaultId\" = @vault AND \"ActiveTo\" IS NULL", ("vault", vault)));

        await Send(admin, HttpMethod.Put, $"/api/admin/users/{janaId}",
            new { displayName = "jana", isActive = true, isWikiAdmin = true, expectedVersion = await UserVersionAsync(admin, janaId) }, HttpStatusCode.OK);
        Directory.Delete(folder, true);
    }

    /// <summary>"Odebrat vše" closes exactly what its preview counts (direct grants, memberships, live tokens) with a fresh sign-in
    /// and keeps the account active; a WikiAdmin lists and revokes anyone's token; the audit filters by person, group and space.</summary>
    [PostgreSqlFact]
    public async Task Revoke_all_admin_tokens_and_audit_filters()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_access_revoke_");
        using var admin = site.Client();
        var adminLogin = (await site.CompleteSetupAsync(admin))["username"]!.GetValue<string>();
        var space = await FirstSpaceAsync(admin);
        var (jana, janaId) = await PersonAsync(site, admin, "jana");
        var group = await GroupAsync(site, admin, TestLogin.For("Provoz"), janaId);
        var page = await PageAsync(admin, space, "Provoz");
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new { userId = janaId, role = "Reader" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/spaces/{space}/access", new { groupId = group, role = "Writer" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/pages/{page}/access/grants", new { userId = janaId, role = "Writer" }, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Put, $"/api/admin/users/{janaId}/api-access", new { canIssueTokens = true, expectedVersion = (string?)null }, HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(jana, site.Password);
        var first = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(jana, HttpMethod.Post, "/api/profile/api-tokens",
            new { name = "Skript", scopes = new[] { "read" }, expiresInDays = 30 }), HttpStatusCode.Created))["token"]!["id"]!.GetValue<Guid>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(jana, HttpMethod.Post, "/api/profile/api-tokens",
            new { name = "Nasazení", scopes = new[] { "read", "write" }, expiresInDays = 30 }), HttpStatusCode.Created);
        var card = await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/principals/user/{janaId}"), HttpStatusCode.OK);
        Assert.Equal(["read", "write"], card["entries"]!.AsArray().Where(e => e!["kind"]!.GetValue<string>() == "token")
            .Select(e => e!["level"]!.GetValue<string>()).Order().ToArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await jana.GetAsync("/api/admin/api-tokens")).StatusCode);
        var allTokens = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/api-tokens"), HttpStatusCode.OK);
        var tokens = allTokens.AsArray().Where(t => t!["userId"]!.GetValue<Guid>() == janaId).ToArray();
        Assert.Equal(2, tokens.Length);
        Assert.All(tokens, t => Assert.Equal("jana", t!["userName"]!.GetValue<string>()));
        await Send(jana, HttpMethod.Delete, $"/api/admin/api-tokens/{first}", null, HttpStatusCode.Forbidden);
        await Send(admin, HttpMethod.Delete, $"/api/admin/api-tokens/{first}", null, HttpStatusCode.NoContent);
        Assert.NotNull((await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/api-tokens"), HttpStatusCode.OK)).AsArray()
            .Single(t => t!["id"]!.GetValue<Guid>() == first)!["revokedAt"]);

        var preview = $"/api/access/principals/user/{janaId}/revoke-all";
        Assert.Equal(HttpStatusCode.Forbidden, (await jana.GetAsync(preview)).StatusCode);
        var impact = await AtlasHttpSite.Json(await admin.GetAsync(preview), HttpStatusCode.OK);
        Assert.Equal((2, 1, 1), (impact["grants"]!.GetValue<int>(), impact["groups"]!.GetValue<int>(), impact["tokens"]!.GetValue<int>()));
        // Group setup now needs fresh authentication; use a new browser session to test an absent window.
        await Send(admin, HttpMethod.Post, "/api/auth/logout", null, HttpStatusCode.NoContent);
        await Send(admin, HttpMethod.Post, "/api/auth/login", new { username = adminLogin, password = site.Password }, HttpStatusCode.OK);
        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, preview);
        Assert.Equal("reauth_required", (await AtlasHttpSite.Json(refused, HttpStatusCode.Forbidden))["code"]!.GetValue<string>());
        await Send(admin, HttpMethod.Post, preview, null, HttpStatusCode.NoContent, await AtlasHttpSite.ReauthAsync(admin, site.Password));
        impact = await AtlasHttpSite.Json(await admin.GetAsync(preview), HttpStatusCode.OK);
        Assert.Equal((0, 0, 0), (impact["grants"]!.GetValue<int>(), impact["groups"]!.GetValue<int>(), impact["tokens"]!.GetValue<int>()));
        Assert.Equal(HttpStatusCode.OK, (await jana.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await jana.GetAsync($"/api/pages/{page}")).StatusCode);
        Assert.Empty((await AtlasHttpSite.Json(await admin.GetAsync($"/api/access/principals/user/{janaId}"), HttpStatusCode.OK))["entries"]!.AsArray());

        var byPerson = (await AtlasHttpSite.Json(await admin.GetAsync($"/api/admin/access-audit?userId={janaId}&limit=100"), HttpStatusCode.OK))["items"]!.AsArray();
        Assert.Contains(byPerson, i => i!["action"]!.GetValue<string>() == "resource_grant.removed");
        Assert.All(byPerson, i => Assert.True(i!["userId"]?.GetValue<Guid>() == janaId || i["actorId"]?.GetValue<Guid>() == janaId));
        var byGroup = (await AtlasHttpSite.Json(await admin.GetAsync($"/api/admin/access-audit?groupId={group}&limit=100"), HttpStatusCode.OK))["items"]!.AsArray();
        Assert.NotEmpty(byGroup);
        Assert.All(byGroup, i => Assert.Equal(group, i!["groupId"]!.GetValue<Guid>()));
        var bySpace = (await AtlasHttpSite.Json(await admin.GetAsync($"/api/admin/access-audit?spaceId={space}&limit=100"), HttpStatusCode.OK))["items"]!.AsArray();
        Assert.Contains(bySpace, i => i!["userId"]?.GetValue<Guid>() == janaId);
        Assert.All(bySpace, i => Assert.Equal(space, i!["spaceId"]!.GetValue<Guid>()));
        Assert.Empty((await AtlasHttpSite.Json(await admin.GetAsync($"/api/admin/access-audit?spaceId={Guid.NewGuid()}"), HttpStatusCode.OK))["items"]!.AsArray());
    }

    private static async Task Send(HttpClient client, HttpMethod method, string path, object? payload, HttpStatusCode expected, string? reauth = null)
    {
        var response = await AtlasHttpSite.SendAsync(client, method, path, payload, reauth);
        Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> FirstSpaceAsync(HttpClient admin)
    {
        return Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
    }

    private static async Task<Guid> SpaceAsync(HttpClient admin, string name)
    {
        return (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name = TestLogin.For(name) }), HttpStatusCode.OK))["id"]!
            .GetValue<Guid>();
    }

    /// <summary>A builtin account, activated and signed in on its own client.</summary>
    private static async Task<(HttpClient Client, Guid Id)> PersonAsync(AtlasHttpSite site, HttpClient admin, string username)
    {
        var login = TestLogin.For(username);
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = login, displayName = username, isWikiAdmin = false }), HttpStatusCode.Created);
        var client = site.Client();
        await Send(client, HttpMethod.Post, "/api/auth/activate", new { token = created["token"]!.GetValue<string>(), newPassword = site.Password },
            HttpStatusCode.NoContent);
        await Send(client, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password }, HttpStatusCode.OK);
        return (client, created["user"]!["id"]!.GetValue<Guid>());
    }

    private static async Task<Guid> GroupAsync(AtlasHttpSite site, HttpClient admin, string name, params Guid[] members)
    {
        var id = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/groups", new { name }), HttpStatusCode.OK))["id"]!
            .GetValue<Guid>();
        var proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        foreach (var member in members)
        {
            await Send(admin, HttpMethod.Put, $"/api/admin/groups/{id}/members/{member}", new { isMember = true, expectedVersion = (string?)null },
                HttpStatusCode.NoContent, proof);
        }
        return id;
    }

    /// <summary>A published page at the space root.</summary>
    private static async Task<Guid> PageAsync(HttpClient admin, Guid spaceId, string title)
    {
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title, contentJson = Rich, markdown = title }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page["id"]}/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        return page["id"]!.GetValue<Guid>();
    }

    private static async Task<string> UserVersionAsync(HttpClient admin, Guid userId)
    {
        return (await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/users"), HttpStatusCode.OK)).AsArray()
            .Single(u => u!["id"]!.GetValue<Guid>() == userId)!["version"]!.GetValue<string>();
    }

    private static JsonNode Entry(JsonNode card, string kind, object id)
    {
        return card["entries"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == kind && e["resourceId"]!.GetValue<string>() == id.ToString())!;
    }

    private static string? Level(JsonNode entry) => entry["level"]?.GetValue<string>();

    private static (string Via, string Label, string Level, bool Effective) Source(JsonNode entry, int index)
    {
        var source = entry["sources"]![index]!;
        return (source["via"]!.GetValue<string>(), source["label"]!.GetValue<string>(), source["level"]!.GetValue<string>(),
            source["effective"]!.GetValue<bool>());
    }

    private static JsonNode? Subject(JsonNode list, Guid id)
    {
        return list["entries"]!.AsArray().SingleOrDefault(e => e!["subject"]!["id"]!.GetValue<Guid>() == id);
    }

    private static JsonNode ListedSpace(JsonNode spaces, Guid id) => spaces.AsArray().Single(s => s!["id"]!.GetValue<Guid>() == id)!;

    private static (string Mode, int GrantCount) Exception(JsonArray exceptions, Guid id)
    {
        var item = exceptions.Single(e => e!["id"]!.GetValue<Guid>() == id)!;
        return (item["mode"]!.GetValue<string>(), item["grantCount"]!.GetValue<int>());
    }
}
