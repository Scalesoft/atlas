using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;

namespace Atlas.Server.Tests.PageAccess;

public sealed class ItemAccessSchemaTests
{
    [PostgreSqlFact]
    public async Task Resource_grant_rejects_invalid_subjects_roles_validity_and_targets()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_checks_");
        var user = await host.SeedUserAsync("user");
        var group = await host.SeedGroupAsync("group");
        var space = await host.SeedSpaceAsync("space");
        var page = await PageAsync(host, space, user);
        await using var connection = await OpenAsync(host);
        string Insert(string subject, int role = 1, string bounds = "NULL,NULL", string target = "") =>
            $"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"UserId\",\"GroupId\",\"Role\",\"StartAt\",\"ExpiresAt\") VALUES (gen_random_uuid(),'{(target == "" ? page.ToString() : target)}',{subject},{role},{bounds})";
        var userOnly = $"'{user.Id}',NULL";
        await RejectedAsync(connection, "23514", Insert($"'{user.Id}','{group}'"));
        await RejectedAsync(connection, "23514", Insert("NULL,NULL"));
        await RejectedAsync(connection, "23514", Insert(userOnly, 0));
        await RejectedAsync(connection, "23514", Insert(userOnly, 4));
        foreach (var bounds in new[] { "now(),now()", "now()+interval '1 day',now()", "'infinity',NULL", "NULL,'-infinity'" })
            await RejectedAsync(connection, "23514", Insert(userOnly, 1, bounds));
        await RejectedAsync(connection, "23503", Insert(userOnly, 1, "NULL,NULL", Guid.NewGuid().ToString()));
        await RejectedAsync(connection, "23503", Insert($"'{Guid.NewGuid()}',NULL"));
        await GrantAsync(connection, page, user.Id, null, 3, "now()", "now()+interval '1 day'");
        await GrantAsync(connection, page, null, group, 1);
    }

    [PostgreSqlFact]
    public async Task Resource_grant_has_one_current_assignment_per_subject_and_no_overlapping_history()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_overlap_");
        var user = await host.SeedUserAsync("user");
        var group = await host.SeedGroupAsync("group");
        var space = await host.SeedSpaceAsync("space");
        var page = await PageAsync(host, space, user);
        var sibling = await PageAsync(host, space, user);
        await using var connection = await OpenAsync(host);
        foreach (var (column, subject) in new[] { ("UserId", user.Id), ("GroupId", group) })
        {
            await ExecuteAsync(connection, $"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"{column}\",\"Role\",\"ActiveFrom\") VALUES (gen_random_uuid(),'{page}','{subject}',1,'2026-01-01Z')");
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"{column}\",\"Role\") VALUES (gen_random_uuid(),'{page}','{subject}',2)"));
            Assert.Contains(duplicate.SqlState, new[] { "23505", "23P01" });
            await RejectedAsync(connection, "23P01", $"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"{column}\",\"Role\",\"ActiveFrom\",\"ActiveTo\") VALUES (gen_random_uuid(),'{page}','{subject}',2,'2025-12-01Z','2026-01-02Z')");
            // Adjacent history and other pages are independent.
            await ExecuteAsync(connection, $"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"{column}\",\"Role\",\"ActiveFrom\",\"ActiveTo\") VALUES (gen_random_uuid(),'{page}','{subject}',2,'2025-12-01Z','2026-01-01Z')");
            await ExecuteAsync(connection, $"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"{column}\",\"Role\") VALUES (gen_random_uuid(),'{sibling}','{subject}',3)");
        }
    }

    [PostgreSqlFact]
    public async Task Every_add_and_removal_of_a_resource_grant_is_audited_with_its_actor()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_audit_");
        var actor = await host.SeedUserAsync("actor");
        var user = await host.SeedUserAsync("user");
        var space = await host.SeedSpaceAsync("Audited", (actor, SpaceRole.Admin));
        var page = await PageAsync(host, space, actor);
        await using var connection = await OpenAsync(host);
        // The handler writes the grant and its audit with its actor (since 3.6 in C#, no trigger).
        host.Actor.Current = actor;
        await host.InvokeAsync(new Atlas.Server.Features.PageAccess.Commands.SetPageGrant(page,
            new Atlas.Server.Features.PageAccess.UpdatePageGrant(user.Id, null, "Writer", null)));
        var grant = (Guid)(await ExecuteAsync(connection, $"SELECT \"Id\" FROM \"ResourceGrant\" WHERE \"PageId\"='{page}'"))!;
        await host.InvokeAsync(new Atlas.Server.Features.PageAccess.Commands.SetPageGrant(page,
            new Atlas.Server.Features.PageAccess.UpdatePageGrant(user.Id, null, null,
                AdministrationVersion.Format((DateTime)(await ExecuteAsync(connection, $"SELECT \"ActiveFrom\" FROM \"ResourceGrant\" WHERE \"Id\"='{grant}'"))!))));

        Assert.Equal($"resource_grant.added,{actor.Id},{user.Id},{space},Audited,2,{page}|resource_grant.removed,{actor.Id},{user.Id},{space},Audited,2,{page}",
            await ExecuteAsync(connection, $"""
                SELECT string_agg(concat_ws(',',"Action","ActorId","UserId","SpaceId","SpaceName","Role","PageId"),'|' ORDER BY "Id")
                FROM "AccessAuditView" WHERE "ResourceGrantId"='{grant}'
                """));
        await RejectedAsync(connection, "23514", $"INSERT INTO \"AccessAuditEvent\"(\"Action\",\"ResourceGrantId\") VALUES ('grant.added','{grant}')");
        await RejectedAsync(connection, "23514", $"INSERT INTO \"AccessAuditEvent\"(\"Action\") VALUES ('resource_grant.added')");
    }

    [PostgreSqlFact]
    public async Task Only_real_access_mode_changes_are_versioned_with_their_actor()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_mode_");
        var actor = await host.SeedUserAsync("actor");
        var space = await host.SeedSpaceAsync("space");
        var page = await PageAsync(host, space, actor);
        var own = await PageAsync(host, space, actor, ownAccess: true);
        await using var connection = await OpenAsync(host);
        async Task<string?> History(Guid id) => (string?)await ExecuteAsync(connection, $"""
            SELECT string_agg(concat_ws(',',"OwnAccess","ChangedBy","ActiveTo" IS NULL),'|' ORDER BY "ActiveFrom")
            FROM "PageAccessModeRevision" WHERE "PageId"='{id}'
            """);
        Assert.Equal("f,t", await History(page));
        Assert.Equal("t,t", await History(own));
        // PageAccessModeHistory (C# since 3.6): another column, or the same mode again, is no new version; a real change is, with its actor.
        async Task WriteAsync(Action<Page> change) => await host.SeedAsync(async session =>
        {
            var loaded = (await session.GetAsync<Page>(page))!;
            change(loaded);
            await PageAccessModeHistory.RecordAsync(session, loaded, actor.Id, CancellationToken.None);
        });
        await WriteAsync(loaded => { loaded.SortOrder += 1; loaded.OwnAccess = false; });
        Assert.Equal("f,t", await History(page));

        await WriteAsync(loaded => loaded.OwnAccess = true);
        Assert.Equal($"f,f|t,{actor.Id},t", await History(page));
        Assert.Equal(true, await ExecuteAsync(connection, $"""
            SELECT bool_and(previous."ActiveTo" = current."ActiveFrom") FROM "PageAccessModeRevision" previous
            JOIN "PageAccessModeRevision" current ON current."PageId" = previous."PageId" AND current."ActiveTo" IS NULL
            WHERE previous."PageId"='{page}' AND previous."ActiveTo" IS NOT NULL
            """));

        using var session = host.OpenSession();
        Assert.True((await session.GetAsync<Page>(page))!.OwnAccess);
    }

    [PostgreSqlFact]
    public async Task Views_filter_validity_name_subjects_and_decide_every_page_on_its_own()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_views_");
        var owner = await host.SeedUserAsync("owner");
        var wiki = await host.SeedUserAsync("wiki", isWikiAdmin: true);
        var admin = await host.SeedUserAsync("admin");
        var reader = await host.SeedUserAsync("reader");
        var member = await host.SeedUserAsync("member");
        var former = await host.SeedUserAsync("former");
        var inactive = await host.SeedUserAsync("inactive");
        var group = await host.SeedGroupAsync("Editors", member, former);
        var space = await host.SeedSpaceAsync("space", (admin, SpaceRole.Admin), (reader, SpaceRole.Reader), (inactive, SpaceRole.Admin));
        var shared = await PageAsync(host, space, owner);
        var own = await PageAsync(host, space, owner, ownAccess: true);
        await using var connection = await OpenAsync(host);
        await ExecuteAsync(connection, $"UPDATE \"GroupMember\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"='{former.Id}'");
        await ExecuteAsync(connection, $"UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\"='{inactive.Id}'");
        var current = await GrantAsync(connection, own, null, group, 2);
        await GrantAsync(connection, own, reader.Id, null, 3, "now()+interval '1 day'");
        await GrantAsync(connection, shared, member.Id, null, 3, "NULL", "now()-interval '1 day'");
        await GrantAsync(connection, shared, inactive.Id, null, 1);

        Assert.Equal(current.ToString(), (await ExecuteAsync(connection, $"SELECT string_agg(\"Id\"::text,',') FROM \"EffectiveResourceGrant\" WHERE \"Role\"<>1 AND \"PageId\" IN ('{own}','{shared}')"))?.ToString());
        Assert.Equal("Editors,2|member,3|reader,3", await ExecuteAsync(connection,
            $"SELECT string_agg(concat_ws(',',\"Name\",\"Role\"),'|' ORDER BY \"Name\") FROM \"ResourceGrantView\" WHERE \"Role\">1 AND \"PageId\" IN ('{own}','{shared}')"));
        var rows = await ExecuteAsync(connection, $"""
            SELECT string_agg(concat_ws(',',u."DisplayName",CASE a."PageId" WHEN '{shared}' THEN 'shared' ELSE 'own' END,a."Role",a."SpaceId"='{space}'),'|'
                ORDER BY u."DisplayName",a."PageId"='{own}')
            FROM "EffectivePageAccess" a JOIN "UserAccount" u ON u."Id"=a."UserId"
            WHERE a."PageId" IN ('{shared}','{own}') AND u."Id" IN ('{owner.Id}','{wiki.Id}','{admin.Id}','{reader.Id}','{member.Id}','{former.Id}','{inactive.Id}')
            """);
        Assert.Equal("admin,shared,3,t|admin,own,3,t|member,own,2,t|reader,shared,1,t|wiki,shared,3,t|wiki,own,3,t", rows);
    }

    [PostgreSqlFact]
    public async Task Effective_views_include_the_start_instant_and_exclude_the_expiry_instant()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_bounds_");
        var owner = await host.SeedUserAsync("owner");
        var starting = await host.SeedUserAsync("starting");
        var expiring = await host.SeedUserAsync("expiring");
        var space = await host.SeedSpaceAsync("space");
        var page = await PageAsync(host, space, owner, ownAccess: true);
        await using var connection = await OpenAsync(host);
        // statement_timestamp() is constant within one client statement and a DO block sees its own inserts, so both
        // grants sit exactly on the instant at which the views are evaluated.
        await ExecuteAsync(connection, $"""
            DO $$
            BEGIN
                INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role","StartAt") VALUES (gen_random_uuid(),'{page}','{starting.Id}',2,statement_timestamp());
                INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role","ExpiresAt") VALUES (gen_random_uuid(),'{page}','{expiring.Id}',2,statement_timestamp());
                CREATE TEMP TABLE bounds AS
                    SELECT 'grant' AS source, "UserId" FROM "EffectiveResourceGrant" WHERE "PageId"='{page}'
                    UNION ALL SELECT 'page', "UserId" FROM "EffectivePageAccess" WHERE "PageId"='{page}' AND "UserId" IN ('{starting.Id}','{expiring.Id}');
            END $$;
            """);
        Assert.Equal($"grant,{starting.Id}|page,{starting.Id}",
            await ExecuteAsync(connection, "SELECT string_agg(concat_ws(',',source,\"UserId\"),'|' ORDER BY source) FROM bounds"));
    }

    [PostgreSqlFact]
    public async Task Page_authorization_decides_own_access_pages_from_direct_grants_and_the_space_admin()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_require_");
        var owner = await host.SeedUserAsync("owner");
        var admin = await host.SeedUserAsync("admin");
        var reader = await host.SeedUserAsync("reader");
        var editor = await host.SeedUserAsync("editor");
        var stranger = await host.SeedUserAsync("stranger");
        var space = await host.SeedSpaceAsync("space", (owner, SpaceRole.Writer), (admin, SpaceRole.Admin), (reader, SpaceRole.Reader), (editor, SpaceRole.Reader));
        var page = await PageAsync(host, space, owner, ownAccess: true);
        var shared = await PageAsync(host, space, owner);
        await using var connection = await OpenAsync(host);
        var grant = await GrantAsync(connection, page, editor.Id, null, 2);
        // Published, so the private draft rule does not hide the pages from non-owners.
        await PublishAsync(connection, page, owner);
        await PublishAsync(connection, shared, owner);

        var (status, edit) = await AuthorizeAsync(host, editor, new ProbePageAccess(page, AccessOperation.EditContent));
        Assert.Equal(200, status);
        Assert.Equal(new[] { grant }, edit!.Decision.GrantIds);
        Assert.True(edit.CanEdit);
        var (adminStatus, manage) = await AuthorizeAsync(host, admin, new ProbePageAccess(page, AccessOperation.ManageAccess));
        Assert.Equal(200, adminStatus);
        Assert.True(manage!.CanEdit);
        // Readable but not permitted: 403. Not readable: 404, whatever the operation.
        Assert.Equal(403, (await AuthorizeAsync(host, editor, new ProbePageAccess(page, AccessOperation.ManageAccess))).Status);
        Assert.Equal(403, (await AuthorizeAsync(host, reader, new ProbePageAccess(shared, AccessOperation.EditContent))).Status);
        Assert.Equal(403, (await AuthorizeAsync(host, owner, new ProbePageAccess(shared, AccessOperation.Share))).Status);
        Assert.Equal(404, (await AuthorizeAsync(host, reader, new ProbePageAccess(page, AccessOperation.Discover))).Status);
        Assert.Equal(404, (await AuthorizeAsync(host, reader, new ProbePageAccess(page, AccessOperation.EditContent))).Status);
        Assert.Equal(404, (await AuthorizeAsync(host, owner, new ProbePageAccess(page, AccessOperation.ReadContent))).Status);
        Assert.Equal(404, (await AuthorizeAsync(host, stranger, new ProbePageAccess(shared, AccessOperation.ManageAccess))).Status);
    }

    [PostgreSqlFact]
    public async Task Private_drafts_on_own_access_pages_stay_hidden_from_a_non_owner_space_admin()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_drafts_");
        var owner = await host.SeedUserAsync("owner");
        var admin = await host.SeedUserAsync("admin");
        var wiki = await host.SeedUserAsync("wiki", isWikiAdmin: true);
        var space = await host.SeedSpaceAsync("space", (owner, SpaceRole.Reader), (admin, SpaceRole.Admin));
        var draft = await PageAsync(host, space, owner, ownAccess: true);
        await using var connection = await OpenAsync(host);
        await GrantAsync(connection, draft, owner.Id, null, 2);

        foreach (var operation in new[] { AccessOperation.ReadContent, AccessOperation.ManageAccess })
        {
            Assert.Equal(404, (await AuthorizeAsync(host, admin, new ProbePageAccess(draft, operation))).Status);
            Assert.Equal(200, (await AuthorizeAsync(host, wiki, new ProbePageAccess(draft, operation))).Status);
        }
        var (status, authorization) = await AuthorizeAsync(host, owner, new ProbePageAccess(draft, AccessOperation.ReadContent));
        Assert.Equal(200, status);
        Assert.True(authorization!.CanEdit);
        Assert.Equal(403, (await AuthorizeAsync(host, owner, new ProbePageAccess(draft, AccessOperation.ManageAccess))).Status);
    }

    [PostgreSqlFact]
    public async Task Page_authorization_requires_the_page_space_to_be_locked()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_lock_");
        var admin = await host.SeedUserAsync("admin");
        var space = await host.SeedSpaceAsync("space", (admin, SpaceRole.Admin));
        var other = await host.SeedSpaceAsync("other", (admin, SpaceRole.Admin));
        var page = await PageAsync(host, space, admin);

        // A page whose space differs from the locked one was moved after the caller resolved the space.
        host.Actor.Current = admin;
        var moved = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PageAuthorization>(
            new ProbePageAccess(page, AccessOperation.ReadContent, LockPageSpace: false, LockSpaceId: other)));
        Assert.Equal((409, "space_changed"), (moved.Status, moved.Code));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InvokeAsync<PageAuthorization>(
            new ProbePageAccess(page, AccessOperation.ReadContent, LockPageSpace: false)));
        Assert.Equal(200, (await AuthorizeAsync(host, admin, new ProbePageAccess(page, AccessOperation.ReadContent))).Status);
    }

    private static async Task<(int Status, PageAuthorization? Authorization)> AuthorizeAsync(AtlasTestHost host, UserAccount user, ProbePageAccess probe)
    {
        host.Actor.Current = user;
        try
        {
            return (200, await host.InvokeAsync<PageAuthorization>(probe));
        }
        catch (ApiException error) when (error.Status is 403 or 404)
        {
            return (error.Status, null);
        }
    }

    private static async Task PublishAsync(NpgsqlConnection connection, Guid page, UserAccount author)
    {
        var revision = Guid.NewGuid();
        await ExecuteAsync(connection, $"""
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
            VALUES ('{revision}','{page}',1,'Page','{"{}"}','Page','{author.Id}',now(),'web');
            UPDATE "Page" SET "PublishedRevisionId"='{revision}' WHERE "Id"='{page}';
            """);
    }
}
