using Npgsql;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>SEC-09: a 1.0 database upgraded through the item access migration keeps every identifier, publication, history row
/// and share link, gains no item grant, and the new page authorization answers exactly as the legacy SpaceRoles/Rules/PageVisibility path.</summary>
public sealed class ItemAccessUpgradeTests
{
    private const int ItemAccessVersion = 2026092601;
    private static readonly string[] Preserved = ["PageRevision", "PagePublication", "PageShareLink", "PageOrganizationRevision", "PageAccessEvent", "SpaceGrant", "GroupMember", "SearchDocument", "AccessAuditEvent"];

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    [PostgreSqlFact]
    public async Task Upgrade_preserves_content_and_sharing_and_matches_the_legacy_oracle()
    {
        // Pooled: the oracle comparison below runs hundreds of short transactions.
        var schema = await DisposableSchema.CreateAsync("atlas_item_upgrade_", pooling: true);
        Dictionary<string, object?> before;
        await using (var connection = new NpgsqlConnection(schema.ConnectionString))
        {
            await connection.OpenAsync();
            await ApplyMigrationsBeforeItemAccessAsync(connection);
            await ExecuteAsync(connection, Fixture);
            before = await SnapshotAsync(connection, "to_jsonb(t)");
            await AtlasMigrations.MigrateAsync(schema.ConnectionString);
            // The only added columns are Page.OwnAccess and AccessAuditEvent.ResourceGrantId, Page.IsTemplate from the later
            // page-template migration, the WP4 V5 audit references, HubChangeId, PagePublication.ChangeSummary (null for every old publication) and the
            // T2 Channel/ApiTokenId and the T3 ApiActivityId/McpSettingId (legacy and null for every old revision and publication, null for every old opening), AiAssisted (false for old revisions), and the AI policy audit references (null for old events), the purge tombstones and audit reference and the backup destination and OIDC settings audit references (null), and the 3.1 Page.TemplatePageId (null for old pages), the 3.6-C version columns of PageRevision (each old revision a version of its own, asserted below) and the 3.6-I Page.PublicationMode (null: inherited) and ShareLink.FollowsLive (false for every old link), and the 3.6 PageRevision.SameAsRevisionId (null: every old version stays listed) and Page.SortChildrenByTitle (false: the manual order); every existing value is unchanged.
            var after = await SnapshotAsync(connection, "to_jsonb(t)-'OwnAccess'-'ResourceGrantId'-'IsTemplate'-'SecretReleaseEventId'-'AccessRequestId'-'ProtectionSettingId'-'TrustedAddressRevisionId'-'SecurityRevisionId'-'ReauthChallengeId'-'HubChangeId'-'ChangeSummary'-'Channel'-'ApiTokenId'-'ApiActivityId'-'McpSettingId'-'AiAssisted'-'UserAiPolicyId'-'GroupAiPolicyId'-'PurgedAt'-'ObjectPurgeEventId'-'BackupDestinationChangeId'-'OidcSettingsRevisionId'-'TemplatePageId'"
                + "-'GroupStartedAt'-'CheckpointCount'-'IsExplicit'-'Label'-'BaseRevisionId'-'AiConversationId'-'PublicationMode'-'FollowsLive'-'SameAsRevisionId'-'SortChildrenByTitle'"
                // GroupId only of PageRevision (the row with GroupStartedAt): SpaceGrant and GroupMember have a GroupId of their own.
                + "-(CASE WHEN to_jsonb(t) ? 'GroupStartedAt' THEN 'GroupId' ELSE '' END)");
            foreach (var table in Preserved.Append("Page"))
                Assert.Equal(before[table], after[table]);
            Assert.Equal(0L, await ExecuteAsync(connection, "SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"HubChangeId\" IS NOT NULL"));
            Assert.Equal(0L, await ExecuteAsync(connection, """
                SELECT count(*) FROM "AccessAuditEvent" WHERE "UserAiPolicyId" IS NOT NULL OR "GroupAiPolicyId" IS NOT NULL
                """));
            Assert.Equal(6L, await ExecuteAsync(connection, "SELECT count(*) FROM \"PageRevision\" WHERE \"AiAssisted\" IS FALSE"));
            Assert.Equal(6L, await ExecuteAsync(connection, """
                SELECT count(*) FROM "PageRevision" r WHERE r."GroupId" = r."Id" AND r."GroupStartedAt" = r."ActiveFrom" AND r."CheckpointCount" = 1
                  AND NOT r."IsExplicit" AND r."Label" IS NULL AND r."AiConversationId" IS NULL
                  AND r."BaseRevisionId" IS NOT DISTINCT FROM (SELECT p."Id" FROM "PageRevision" p WHERE p."PageId" = r."PageId" AND p."Version" = r."Version" - 1)
                """));
            Assert.Equal(0L, await ExecuteAsync(connection, "SELECT count(*) FROM \"Page\" WHERE \"PublicationMode\" IS NOT NULL"));
            Assert.Equal(0L, await ExecuteAsync(connection, "SELECT count(*) FROM \"PageRevision\" WHERE \"SameAsRevisionId\" IS NOT NULL"));
            Assert.Equal(0L, await ExecuteAsync(connection, "SELECT count(*) FROM \"Page\" WHERE \"SortChildrenByTitle\""));
            Assert.Equal(0L, await ExecuteAsync(connection, "SELECT count(*) FROM \"ResourceGrant\""));
            Assert.Equal(0L, await ExecuteAsync(connection, "SELECT count(*) FROM \"Page\" WHERE \"OwnAccess\""));
            Assert.Equal(5L, await ExecuteAsync(connection, """
                SELECT count(*) FROM "Page" p JOIN "PageAccessModeRevision" r ON r."PageId"=p."Id"
                WHERE r."ActiveTo" IS NULL AND NOT r."OwnAccess" AND r."ChangedBy" IS NULL
                """));
            Assert.Equal(5L, await ExecuteAsync(connection, "SELECT count(*) FROM \"PageAccessModeRevision\""));
        }

        await using var host = await AtlasTestHost.CreateAsync(schema);
        using var session = host.OpenSession();
        var pages = new[] { Id(40), Id(41), Id(42), Id(43), Id(44) };
        var outcomes = new Dictionary<int, int>();
        foreach (var userId in Enumerable.Range(1, 11).Select(Id))
        {
            var user = (await session.GetAsync<UserAccount>(userId))!;
            foreach (var pageId in pages)
            {
                var page = (await session.GetAsync<Page>(pageId))!;
                host.Actor.Current = user;
                foreach (var (operation, required) in Operations)
                {
                    var legacy = await LegacyAsync(host, page, user, required);
                    var current = await CurrentAsync(host, page.Id, operation);
                    Assert.True(legacy.Status == current.Status, $"{user.Username} {pageId} {operation}: legacy {legacy.Status}, current {current.Status}");
                    if (current.Authorization is not null)
                        Assert.Equal(legacy.CanEdit, current.Authorization.CanEdit);
                    outcomes[current.Status] = outcomes.GetValueOrDefault(current.Status) + 1;
                }
            }
        }
        Assert.Equal(275, outcomes.Values.Sum());
        Assert.All(new[] { 200, 403, 404 }, status => Assert.True(outcomes.GetValueOrDefault(status) > 0, $"no {status} outcome"));
    }

    private static readonly (AccessOperation Operation, SpaceRole Required)[] Operations =
    [
        (AccessOperation.Discover, SpaceRole.Reader), (AccessOperation.ReadContent, SpaceRole.Reader),
        (AccessOperation.EditContent, SpaceRole.Writer), (AccessOperation.ManageAccess, SpaceRole.Admin),
        (AccessOperation.Share, SpaceRole.Admin),
    ];

    /// <summary>The 1.0 path: space role under the locked actor, the legacy matrix and the private draft rule, with the S2
    /// status rule: 404 unless the page is visible and readable, 403 when readable but the role is too low.</summary>
    private static async Task<(int Status, bool CanEdit)> LegacyAsync(AtlasTestHost host, Page page, UserAccount user, SpaceRole required)
    {
        SpaceRole role;
        try
        {
            role = await host.InvokeAsync<SpaceRole>(new ProbeAccess(page.SpaceId, page.SpaceId, SpaceRole.Reader));
        }
        catch (ApiException error) when (error.Status is 401 or 404)
        {
            return (404, false);
        }
        if (!PageVisibility.IsVisible(page, user.Id, user.IsWikiAdmin))
            return (404, false);
        return role >= required ? (200, role >= SpaceRole.Writer) : (403, false);
    }

    private static async Task<(int Status, PageAuthorization? Authorization)> CurrentAsync(AtlasTestHost host, Guid pageId, AccessOperation operation)
    {
        try
        {
            return (200, await host.InvokeAsync<PageAuthorization>(new ProbePageAccess(pageId, operation)));
        }
        catch (ApiException error) when (error.Status is 401 or 403 or 404)
        {
            return (error.Status == 401 ? 404 : error.Status, null);
        }
    }

    private static async Task ApplyMigrationsBeforeItemAccessAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE TABLE \"SchemaVersion\"(\"Version\" bigint PRIMARY KEY,\"AppliedAt\" timestamptz NOT NULL DEFAULT now(),\"Description\" varchar(1024))");
        var assembly = typeof(AtlasMigrations).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
            .Where(x => x.Version < ItemAccessVersion).OrderBy(x => x.Version))
        {
            await using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connection, await reader.ReadToEndAsync());
            await ExecuteAsync(connection, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
    }

    private static async Task<Dictionary<string, object?>> SnapshotAsync(NpgsqlConnection connection, string row)
    {
        var result = new Dictionary<string, object?>();
        foreach (var table in Preserved.Append("Page"))
            result[table] = await ExecuteAsync(connection, $"SELECT jsonb_agg({row} ORDER BY to_jsonb(t)->>'Id', to_jsonb(t)->>'PageId')::text FROM \"{table}\" t");
        return result;
    }

    // A 1.0 wiki: direct, group (Writer and Admin), direct plus group, bounded, future, expired, closed and inactive grants;
    // published, child, draft, trashed and other-space pages; closed revision and publication history; an active share link.
    private static readonly string Fixture = $"""
        INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
            ('{Id(1)}','wiki','Wiki',true,true,'stamp'), ('{Id(2)}','owner','Owner',false,true,'stamp'),
            ('{Id(3)}','reader','Reader',false,true,'stamp'), ('{Id(4)}','member','Member',false,true,'stamp'),
            ('{Id(5)}','future','Future',false,true,'stamp'), ('{Id(6)}','expired','Expired',false,true,'stamp'),
            ('{Id(7)}','inactive','Inactive',false,false,'stamp'), ('{Id(8)}','manager','Manager',false,true,'stamp'),
            ('{Id(9)}','former','Former',false,true,'stamp'), ('{Id(10)}','outsider','Outsider',false,true,'stamp'),
            ('{Id(11)}','groupadmin','Group admin',false,true,'stamp');
        INSERT INTO "UserGroup"("Id","Name") VALUES ('{Id(20)}','Editors'), ('{Id(24)}','Admins');
        INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom","ActiveTo") VALUES
            ('{Id(21)}','{Id(20)}','{Id(4)}','2026-01-01Z',NULL), ('{Id(22)}','{Id(20)}','{Id(10)}','2020-01-01Z','2021-01-01Z'),
            ('{Id(25)}','{Id(24)}','{Id(11)}','2026-01-01Z',NULL);
        INSERT INTO "Space"("Id","Name","Icon","Color") VALUES ('{Id(30)}','Alpha','book-open','blue'), ('{Id(31)}','Beta','book-open','blue');
        INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","GroupId","Role","ActiveFrom","ActiveTo","StartAt","ExpiresAt") VALUES
            ('{Id(80)}','{Id(30)}','{Id(2)}',NULL,2,'2026-01-01Z',NULL,NULL,NULL),
            ('{Id(81)}','{Id(30)}','{Id(3)}',NULL,1,'2026-01-01Z',NULL,NULL,NULL),
            ('{Id(82)}','{Id(31)}','{Id(3)}',NULL,2,'2026-01-01Z',NULL,NULL,NULL),
            ('{Id(83)}','{Id(30)}',NULL,'{Id(20)}',2,'2026-01-01Z',NULL,NULL,NULL),
            ('{Id(84)}','{Id(30)}','{Id(5)}',NULL,1,'2026-01-01Z',NULL,now()+interval '1 day',NULL),
            ('{Id(85)}','{Id(30)}','{Id(6)}',NULL,2,'2026-01-01Z',NULL,NULL,now()-interval '1 day'),
            ('{Id(86)}','{Id(30)}','{Id(7)}',NULL,3,'2026-01-01Z',NULL,NULL,NULL),
            ('{Id(87)}','{Id(30)}','{Id(8)}',NULL,3,'2026-01-01Z',NULL,now()-interval '1 day',now()+interval '30 days'),
            ('{Id(88)}','{Id(30)}','{Id(9)}',NULL,3,'2020-01-01Z','2021-01-01Z',NULL,NULL),
            ('{Id(89)}','{Id(30)}','{Id(9)}',NULL,1,'2021-01-01Z',NULL,NULL,NULL),
            ('{Id(90)}','{Id(30)}',NULL,'{Id(24)}',3,'2026-01-01Z',NULL,NULL,NULL),
            ('{Id(91)}','{Id(30)}','{Id(4)}',NULL,1,'2026-01-01Z',NULL,NULL,NULL);
        INSERT INTO "Page"("Id","SpaceId","ParentId","OwnerId","Version") VALUES
            ('{Id(40)}','{Id(30)}',NULL,'{Id(2)}',2), ('{Id(41)}','{Id(30)}','{Id(40)}','{Id(2)}',1), ('{Id(42)}','{Id(30)}',NULL,'{Id(2)}',1),
            ('{Id(43)}','{Id(30)}',NULL,'{Id(2)}',1), ('{Id(44)}','{Id(31)}',NULL,'{Id(3)}',1);
        INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo") VALUES
            ('{Id(50)}','{Id(40)}',1,'Handbook','{Rich}','Handbook v1','{Id(2)}','2026-01-01Z','2026-02-01Z'),
            ('{Id(51)}','{Id(40)}',2,'Handbook','{Rich}','Handbook v2','{Id(4)}','2026-02-01Z',NULL),
            ('{Id(52)}','{Id(41)}',1,'Chapter','{Rich}','Chapter','{Id(2)}','2026-01-01Z',NULL),
            ('{Id(53)}','{Id(42)}',1,'Draft','{Rich}','Draft','{Id(2)}','2026-01-01Z',NULL),
            ('{Id(54)}','{Id(43)}',1,'Old','{Rich}','Old','{Id(2)}','2026-01-01Z',NULL),
            ('{Id(55)}','{Id(44)}',1,'Beta page','{Rich}','Beta page','{Id(3)}','2026-01-01Z',NULL);
        INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","ActiveTo") VALUES
            ('{Id(60)}','{Id(40)}','{Id(50)}','{Id(2)}','2026-01-01Z','2026-02-01Z'),
            ('{Id(61)}','{Id(40)}','{Id(51)}','{Id(4)}','2026-02-01Z',NULL),
            ('{Id(62)}','{Id(41)}','{Id(52)}','{Id(2)}','2026-01-01Z',NULL),
            ('{Id(63)}','{Id(43)}','{Id(54)}','{Id(2)}','2026-01-01Z',NULL),
            ('{Id(64)}','{Id(44)}','{Id(55)}','{Id(3)}','2026-01-01Z',NULL);
        UPDATE "Page" p SET "PublishedRevisionId"=x."RevisionId" FROM "PagePublication" x WHERE x."PageId"=p."Id" AND x."ActiveTo" IS NULL;
        UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"='{Id(2)}',"TrashBatchId"='{Id(45)}' WHERE "Id"='{Id(43)}';
        INSERT INTO "SpaceExternalSharingPolicy"("Id","SpaceId","Enabled","ChangedById","ActiveFrom") VALUES ('{Id(70)}','{Id(30)}',true,'{Id(1)}',now());
        INSERT INTO "PageShareLink"("Id","SpaceId","PageId","RevisionId","PolicyId","CreatedById","CreatorSecurityStamp","Name","TokenHash","ExpiresAt","ActiveFrom")
            VALUES ('{Id(71)}','{Id(30)}','{Id(40)}','{Id(51)}','{Id(70)}','{Id(1)}','stamp','Partner','{new string('A', 64)}',now()+interval '7 days',now());
        """;

    private const string Rich = """{"format":"scalesoft-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
}
