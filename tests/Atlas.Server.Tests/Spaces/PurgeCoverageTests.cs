using System.Text.RegularExpressions;
using Npgsql;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

/// <summary>
/// The completeness of a permanent delete (unified trash spec §2; ARCH3-2, ARCH3-3), on a host with every optional module off: the
/// boards and the projector's index of a purged page go even so, nothing is reindexed for a tombstone at the commit (PER3-4), a
/// publication keeps no change summary (PER3-1), a reference node elsewhere pointing at it goes (PER3-5), and the audit counts the
/// boards (SEC3-6). Every table that names a page or a space is either written by the purge (as recorded), removed with one by its
/// cascade, or kept on purpose with the reason below; a new table fails here until someone decides.
/// </summary>
public sealed partial class PurgeCoverageTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"tajné"}]}]}}""";

    /// <summary>Tables that keep their rows of a purged page or space, and why. Content never stays: a row here holds ids, an audit
    /// fact or an account's own data.</summary>
    private static readonly Dictionary<string, string> Kept = new(StringComparer.Ordinal)
    {
        ["AiMessage"] = "an account's own conversation; the scope names the page only, retention ends it",
        ["AiSourceDependency"] = "ids of the sources of an account's answer, with its message",
        ["ApiActivity"] = "the access audit of API and MCP writes",
        ["ContactLinkRun"] = "the progress of the contacts' link migration: its cursor is a page id, no content",
        ["GitBackupPage"] = "the Git backup's state, so its next run commits the deletion",
        ["JobRun"] = "the run log of a background job: the space is an id, the retention ages the run (30 days, 20 runs a job)",
        ["ObjectPurgeEvent"] = "the audit of the purge itself",
        ["PageAccessEvent"] = "immutable access history that names the tombstone",
        ["PageAccessModeRevision"] = "immutable access history that names the tombstone",
        ["PageOrganizationRevision"] = "the organization history (who moved or trashed what), no content",
        ["PageShareLink"] = "the history of external links; the links stop with the page",
        ["ProjectorCatalogEvent"] = "the audit of who marked the projector catalog",
        ["SecretReleaseEvent"] = "the access audit of password releases",
        ["SpaceExternalSharingPolicy"] = "the history of a space setting, no content",
        ["VaultAccessRequest"] = "the access audit of vault requests",
        ["WikiImportCheckpoint"] = "ids of the Wiki.js import, so a rerun does not import the page again",
        ["WikiImportDestination"] = "ids of the Wiki.js import destination",
        ["WikiImportPage"] = "ids of the Wiki.js import, so a rerun does not import the page again",
        ["ContentResource"] = "a page's envelope is its tombstone's; the space's objects are deleted",
        ["PageLink"] = "links from live pages to the tombstone read as a missing page; its own links go",
    };

    [Trait("Category", "Guard")]
    [PostgreSqlFact]
    public async Task A_purge_with_every_module_off_leaves_no_module_data_and_every_table_is_decided()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_purge_coverage_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var space = await host.SeedSpaceAsync("Provoz");
        var other = await host.SeedSpaceAsync("Jinde");
        host.Actor.Current = admin;
        var page = await CreatePageAsync(host, space, null, "Plán", "Popis změny s tajemstvím");
        var child = await CreatePageAsync(host, space, page, "Podstránka");
        var kept = await CreatePageAsync(host, space, null, "Zůstane");
        var board = Guid.NewGuid();
        // A board under the page, written while the Boards module was on; switched off now, nothing trashes it with its page.
        await SqlAsync(host, """
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","ParentPageId","SortOrder")
              VALUES (@board, @space, 'Architektura', 1, now(), @admin, now(), false, @child, 4096);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Version","IsDeleted","PropsJson","SearchText")
              VALUES (gen_random_uuid(), @board, 'note', 0, 0, 100, 100, 1, 'VPN heslo', 1, false, '{}', 'VPN heslo');
            INSERT INTO "TreeReference"("Id","SpaceId","ParentPageId","SortOrder","TargetId","Anchor","CreatedBy","CreatedAt")
              VALUES (gen_random_uuid(), @other, NULL, 1024, @child, NULL, @admin, now());
            """, new { board, space, admin = admin.Id, child, other });
        // The requirement index is kept in code (migration 2026110870): a read makes the pages current, so the purge has marks to remove.
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, null, default);
            await transaction.CommitAsync();
        }
        Assert.True(await CountAsync(host, """SELECT count(*) FROM "ProjectorIndexedRevision" WHERE "PageId" = @page""", new { page }) > 0);

        await host.InvokeAsync(new TrashPage(page, new TrashPageRequest(true, await TreeVersionAsync(host, space), await VersionAsync(host, page))));
        var statements = new List<string>();
        using (var recording = SqlRecording.Start())
        {
            await host.InvokeAsync(new PurgeObject(page));
            statements.AddRange(recording.Statements);
        }

        var pages = new { pages = new[] { page, child } };
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "Board" WHERE "Id" = @board""", new { board }));
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "BoardItem" WHERE "BoardId" = @board""", new { board }));
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "ContentResource" WHERE "Id" = @board""", new { board }));
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "ProjectorIndexedRevision" WHERE "PageId" = ANY(@pages)""", pages));
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "PagePublication" WHERE "PageId" = ANY(@pages) AND "ChangeSummary" IS NOT NULL""", pages));
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "TreeReference" WHERE "TargetId" = ANY(@pages)""", pages));
        Assert.Equal(2L, await CountAsync(host, """SELECT count(*) FROM "Page" WHERE "Id" = ANY(@pages) AND "PurgedAt" IS NOT NULL""", pages));
        Assert.Equal("""{"pages": 2, "boards": 1}""", await ScalarAsync(host, """SELECT CAST("Detail" AS text) FROM "ObjectPurgeEvent" WHERE "ObjectId" = @page""", new { page }));

        await host.InvokeAsync(new TrashSpace(space, new TrashSpaceRequest("Provoz")));
        using (var recording = SqlRecording.Start())
        {
            await host.InvokeAsync(new PurgeSpace(space, new PurgeSpaceRequest("Provoz")));
            statements.AddRange(recording.Statements);
        }
        Assert.Equal(0L, await CountAsync(host, """SELECT count(*) FROM "ProjectorIndexedRevision" WHERE "PageId" = @kept""", new { kept }));
        Assert.Contains("\"boards\": 0", await ScalarAsync(host, """SELECT CAST("Detail" AS text) FROM "ObjectPurgeEvent" WHERE "ObjectId" = @space""", new { space }));

        var written = statements.SelectMany(sql => Written().Matches(sql))
            .ToLookup(match => match.Groups[1].Value.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase), match => match.Groups[2].Value);
        var tables = await NamingTablesAsync(host.ConnectionString);
        var cascades = await CascadesAsync(host.ConnectionString);
        // Updated in place (a tombstone, an emptied revision) or deleted; only a delete takes the rows that cascade from it along.
        var deleted = new HashSet<string>(written[true], StringComparer.Ordinal);
        while (cascades.Where(x => deleted.Contains(x.Target) && !deleted.Contains(x.Table)).Select(x => x.Table).ToList() is { Count: > 0 } more)
        {
            deleted.UnionWith(more);
        }
        var covered = new HashSet<string>(deleted.Concat(written[false]), StringComparer.Ordinal);
        Assert.Empty(tables.Where(table => !covered.Contains(table) && !Kept.ContainsKey(table)).Order(StringComparer.Ordinal));
        // A reason for a table that is gone or written after all is stale.
        Assert.Empty(Kept.Keys.Where(table => !tables.Contains(table) || covered.Contains(table) && table is not ("ContentResource" or "PageLink")).Order(StringComparer.Ordinal));
    }

    /// <summary>SEC3-2, SEC-Z-1: a manage grant on one page reads no space trash, so it neither restores from it (the space trash's own
    /// restore and the object path's) nor deletes for good; the space Admin and the wiki admin do (the rights matrix of the page trash).</summary>
    [PostgreSqlFact]
    public async Task Only_a_space_admin_or_a_wiki_admin_restores_or_purges_a_page_a_page_manager_cannot()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_purge_rights_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var spaceAdmin = await host.SeedUserAsync("spaceadmin");
        var writer = await host.SeedUserAsync("writer");
        var manager = await host.SeedUserAsync("manager");
        var space = await host.SeedSpaceAsync("Provoz", (spaceAdmin, SpaceRole.Admin), (writer, SpaceRole.Writer), (manager, SpaceRole.Writer));
        host.Actor.Current = admin;
        var first = await CreatePageAsync(host, space, null, "První");
        var second = await CreatePageAsync(host, space, null, "Druhá");
        foreach (var page in new[] { first, second })
        {
            await host.InvokeAsync(new SetPageGrant(page, new UpdatePageGrant(manager.Id, null, "Admin", null)));
            await host.InvokeAsync(new TrashPage(page, new TrashPageRequest(false, await TreeVersionAsync(host, space), await VersionAsync(host, page))));
        }

        foreach (var (refused, status) in new[] { (manager, 403), (writer, 403) })
        {
            host.Actor.Current = refused;
            var tree = await TreeVersionAsync(host, space);
            Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync(new UntrashPage(first, new UntrashPageRequest(null, false, tree))))).Status);
            Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<ObjectChangeResult>(new RestoreObject(first)))).Status);
            Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync(new PurgeObject(first)))).Status);
        }
        Assert.Equal(1L, await CountAsync(host, """SELECT count(*) FROM "Page" WHERE "Id" = @first AND "IsTrashed" AND "PurgedAt" IS NULL""", new { first }));
        host.Actor.Current = spaceAdmin;
        await host.InvokeAsync(new PurgeObject(first));
        host.Actor.Current = admin;
        await host.InvokeAsync(new PurgeObject(second));
        Assert.Equal(2L, await CountAsync(host, """SELECT count(*) FROM "Page" WHERE "Id" IN (@first, @second) AND "PurgedAt" IS NOT NULL""", new { first, second }));
    }

    /// <summary>PERF3-2: the tombstones are one statement, so a space of 30 pages costs what a space of 5 does.</summary>
    [PostgreSqlFact]
    public async Task A_purge_costs_the_same_statements_for_few_and_many_pages()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_purge_cost_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        host.Actor.Current = admin;

        async Task<int> StatementsAsync(string name, int count)
        {
            var space = await host.SeedSpaceAsync(name);
            Guid? parent = null;
            for (var index = 0; index < count; index++)
            {
                var page = await CreatePageAsync(host, space, index % 2 == 0 ? null : parent, $"Stránka {index}");
                parent = page;
            }
            await host.InvokeAsync(new TrashSpace(space, new TrashSpaceRequest(name)));
            using var recording = SqlRecording.Start();
            await host.InvokeAsync(new PurgeSpace(space, new PurgeSpaceRequest(name)));
            return recording.Statements.Count;
        }

        var few = await StatementsAsync("Malý", 5);
        var many = await StatementsAsync("Velký", 30);
        Assert.True(many == few, $"purge: {few} SQL for 5 pages, {many} for 30");
    }

    [GeneratedRegex("""(DELETE\s+FROM|UPDATE)\s+"(\w+)"\s""", RegexOptions.IgnoreCase)]
    private static partial Regex Written();

    /// <summary>Base tables with a column that names a page or a space, or a reference to Page or Space.</summary>
    private static async Task<HashSet<string>> NamingTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT c.table_name FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = current_schema() AND c.column_name ~ '(PageId|SpaceId)$'
            UNION
            SELECT conrelid::regclass::text FROM pg_constraint
            WHERE contype = 'f' AND confrelid IN (to_regclass('"Page"'), to_regclass('"Space"'))
            """, connection);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0).Trim('"'));
        }
        return tables;
    }

    /// <summary>Which table loses its rows with another's (ON DELETE CASCADE).</summary>
    private static async Task<List<(string Table, string Target)>> CascadesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conrelid::regclass::text, confrelid::regclass::text FROM pg_constraint WHERE contype = 'f' AND confdeltype = 'c'
            """, connection);
        var cascades = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cascades.Add((reader.GetString(0).Trim('"'), reader.GetString(1).Trim('"')));
        }
        return cascades;
    }

    private static async Task<Guid> CreatePageAsync(AtlasTestHost host, Guid space, Guid? parent, string title, string? summary = null)
    {
        var page = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, parent, title, Rich, "tajné")));
        await host.InvokeAsync<PageDto>(new PublishPage(page.Id, new PublishPageRequest(page.Version, summary)));
        return page.Id;
    }

    private static async Task<int> VersionAsync(AtlasTestHost host, Guid page) => (await host.InvokeAsync<PageDto>(new GetPage(page))).Version;

    private static async Task<long> TreeVersionAsync(AtlasTestHost host, Guid space) =>
        (long)(await ScalarAsync(host, """SELECT CAST("Version" AS text) FROM "SpaceOrganization" WHERE "SpaceId" = @space""", new { space }) is { } text ? long.Parse(text) : 0);

    private static async Task<long> CountAsync(AtlasTestHost host, string sql, object parameters) => long.Parse((await ScalarAsync(host, sql, parameters))!);

    private static async Task<string?> ScalarAsync(AtlasTestHost host, string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT CAST(({sql}) AS text)", connection);
        Bind(command, parameters);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private static async Task SqlAsync(AtlasTestHost host, string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        Bind(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static void Bind(NpgsqlCommand command, object parameters)
    {
        foreach (var property in parameters.GetType().GetProperties())
        {
            command.Parameters.AddWithValue(property.Name, property.GetValue(parameters)!);
        }
    }
}
