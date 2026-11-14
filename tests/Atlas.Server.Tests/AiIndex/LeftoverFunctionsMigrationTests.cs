using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.AiIndex;

/// <summary>
/// Migration 2026110950 (no logic in the database, S5) over an installation that holds a page, a contact and an index with its
/// neighbour lists: the functions go (the index's eligibility, the neighbour refreshes and their helpers, the catalog's resource list),
/// every stored row stays as it was, and nothing is rewritten. What the functions computed is pinned by PageNeighborParityTests (the
/// lists), the index tests (eligibility) and EntityCatalogTests (the rebuild's resources).
/// </summary>
public sealed class LeftoverFunctionsMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
    private const string Page = "00000000-0000-0000-0000-0000000000a1", Contact = "00000000-0000-0000-0000-0000000000c1";

    private static readonly string[] Functions = ["atlas_ai_index_eligible", "atlas_ai_index_candidate", "atlas_refresh_page_neighbors",
        "atlas_refresh_generation_neighbors", "atlas_insert_own_page_neighbors", "atlas_lock_page_neighbors", "atlas_entity_catalog_resources",
        "atlas_ai_dependency_status"];

    [PostgreSqlFact]
    public async Task Migration_2026110950_drops_the_functions_and_keeps_every_row()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_leftover_functions_");
        var db = schema.ConnectionString;
        await OutboxTests.ApplyBeforeAsync(db, 2026110950);
        await OutboxTests.ExecuteAsync(db, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{Owner}}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{{Space}}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{{Page}}','{{Space}}','{{Owner}}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('00000000-0000-0000-0000-0000000000b1','{{Page}}',1,'Old','{"type":"contact"}','old','{{Owner}}',now(),'web');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","CreatedAt","Title","Data")
              VALUES('{{Contact}}','atlas.contact','wiki',now(),'Jana','{"name":"Jana"}'),
                    ('{{Page}}','atlas.page','wiki',now(),'Old',NULL);
            """);
        var listed = await OutboxTests.ScalarAsync(db, """SELECT string_agg("Id"::text, ',' ORDER BY "Id") FROM atlas_entity_catalog_resources()""");
        Assert.Equal($"{Page},{Contact}", listed);
        var kept = await RowsAsync(db);

        await OutboxTests.ApplyBeforeAsync(db, 2026110951, from: 2026110950);

        Assert.Equal(0L, await OutboxTests.ScalarAsync(db,
            $"SELECT count(*) FROM pg_proc WHERE proname IN ({string.Join(',', Functions.Select(name => $"'{name}'"))})"));
        Assert.Equal(kept, await RowsAsync(db));
    }

    private static async Task<object?> RowsAsync(string db) => await OutboxTests.ScalarAsync(db, """
        SELECT (SELECT count(*) FROM "ContentResource") || ':' || (SELECT count(*) FROM "Page") || ':' || (SELECT count(*) FROM "PageRevision")
        """);
}
