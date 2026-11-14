using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>Migration 2026110470 over the boards an installation already has: a page's board of the purpose projector.deployment goes
/// with its items; the requirement cards of other boards and the connectors on them are deleted, the other items, the page and its own
/// other boards stay, and a frame loses the lane mark of the removed planner; the board's visits and favourites go, the search follows,
/// and a board that changed moves its sequence on, so an open client reloads it instead of keeping the deleted cards.</summary>
public sealed class DropDeploymentBoardsMigrationTests
{
    private const long Version = 2026110470;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
        Page = "00000000-0000-0000-0000-0000000000f1", Deployment = "00000000-0000-0000-0000-0000000000b1", Plan = "00000000-0000-0000-0000-0000000000b2";

    [PostgreSqlFact]
    public async Task Deployment_boards_and_requirement_cards_go_and_everything_else_stays()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_drop_deployment_boards_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{Writer}}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{{Space}}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{{Page}}','{{Space}}','{{Writer}}',1);
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder","Purpose") VALUES
              ('{{Deployment}}','{{Space}}','{{Page}}','Tabule nasazení: Portál',3,now(),'{{Writer}}',now(),false,1,'projector.deployment');
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('{{Plan}}','{{Space}}','{{Page}}','Plan',6,now(),'{{Writer}}',now(),false,1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","TargetResourceId","FromItemId","ToItemId","Version","IsDeleted","PropsJson") VALUES
              ('00000000-0000-0000-0000-0000000000c1','{{Deployment}}','page',0,0,280,128,0,NULL,'{{Page}}',NULL,NULL,1,false,'{"display":"requirement","anchor":"a"}'),
              ('00000000-0000-0000-0000-0000000000c2','{{Plan}}','page',0,0,280,128,0,NULL,'{{Page}}',NULL,NULL,1,false,'{"display":"requirement","anchor":"a"}'),
              ('00000000-0000-0000-0000-0000000000c3','{{Plan}}','page',300,0,280,128,1,NULL,'{{Page}}',NULL,NULL,2,false,'{"display":"card"}'),
              ('00000000-0000-0000-0000-0000000000c4','{{Plan}}','note',0,200,160,160,2,'Hi',NULL,NULL,NULL,3,false,'{}'),
              ('00000000-0000-0000-0000-0000000000c5','{{Plan}}','frame',0,400,400,400,3,'Portál',NULL,NULL,NULL,4,false,'{"lane":"Portál"}');
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","FromItemId","ToItemId","Version","IsDeleted","PropsJson") VALUES
              ('00000000-0000-0000-0000-0000000000d1','{{Plan}}','connector',0,0,8,8,4,'00000000-0000-0000-0000-0000000000c4','00000000-0000-0000-0000-0000000000c2',5,false,'{}'),
              ('00000000-0000-0000-0000-0000000000d2','{{Plan}}','connector',0,0,8,8,5,'00000000-0000-0000-0000-0000000000c4','00000000-0000-0000-0000-0000000000c3',6,false,'{}');
            UPDATE "BoardItem" SET "SearchText" = 'Hi' WHERE "Id" = '00000000-0000-0000-0000-0000000000c4';
            UPDATE "BoardItem" SET "SearchText" = 'Spojka' WHERE "Id" = '00000000-0000-0000-0000-0000000000d1';
            INSERT INTO "PageFavorite"("Id","UserId","PageId","ActiveFrom") VALUES (gen_random_uuid(),'{{Writer}}','{{Deployment}}',now());
            INSERT INTO "PageVisit"("Id","UserId","PageId","LastVisitedAt") VALUES
              (gen_random_uuid(),'{{Writer}}','{{Deployment}}',now()), (gen_random_uuid(),'{{Writer}}','{{Page}}',now());
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, $"""SELECT count(*) FROM "Board" WHERE "Id"='{Deployment}'"""));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, $"""SELECT count(*) FROM "BoardItem" WHERE "BoardId"='{Deployment}'"""));
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, $"""SELECT count(*) FROM "Board" WHERE "Id"='{Plan}'"""));
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, $"""SELECT count(*) FROM "Page" WHERE "Id"='{Page}'"""));
        Assert.Equal("c2,d1", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right("Id"::text,2),',' ORDER BY "Id") FROM "BoardItem" WHERE "IsDeleted"
            """));
        Assert.Equal("c3,c4,c5,d2", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right("Id"::text,2),',' ORDER BY "Id") FROM "BoardItem" WHERE NOT "IsDeleted"
            """));
        Assert.Equal("{}", await ScalarAsync(schema.ConnectionString, """SELECT "PropsJson"::text FROM "BoardItem" WHERE "Kind"='frame'"""));
        // A visit and a favourite of the board go with it (FK to its envelope); the page's own visit stays; the plan's search drops the connector.
        Assert.Equal("0,1", await ScalarAsync(schema.ConnectionString, $"""
            SELECT (SELECT count(*) FROM "PageFavorite" WHERE "PageId"='{Deployment}') + (SELECT count(*) FROM "PageVisit" WHERE "PageId"='{Deployment}')
              || ',' || (SELECT count(*) FROM "PageVisit" WHERE "PageId"='{Page}')
            """));
        Assert.Equal("Hi", await ScalarAsync(schema.ConnectionString, $"""SELECT "Body" FROM "BoardSearchDocument" WHERE "BoardId"='{Plan}'"""));
        // No batch logs the change: the sequence past the log makes a client catching up from 6 get resync_required (GetBoardOperations).
        Assert.Equal(7L, await ScalarAsync(schema.ConnectionString, $"""SELECT "Sequence" FROM "Board" WHERE "Id"='{Plan}'"""));
    }

    /// <summary>Applies the embedded scripts below a version, with their journal, as the previous release left the schema.</summary>
    private static async Task ApplyBeforeAsync(string connectionString, long version)
    {
        await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < version).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
            await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
