using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>Migration 2026102896 over the items a board already has: their colours stay; an item now also takes a colour of the
/// appearance palette (<c>blue-4</c>), never a tone outside it or one of the palette's legacy names.</summary>
public sealed class BoardItemAppearanceColorsMigrationTests
{
    private const long Version = 2026102896;

    [PostgreSqlFact]
    public async Task Existing_colours_stay_and_the_appearance_palette_is_admitted()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_board_colors_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Plan',3,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Color","Version","IsDeleted") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1','note',0,0,160,160,0,'Hi','yellow',1,false),
              ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b1','note',200,0,160,160,1,NULL,NULL,2,false),
              ('00000000-0000-0000-0000-0000000000c3','00000000-0000-0000-0000-0000000000b1','text',0,200,240,40,2,'Tma','black',3,false);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("c1=yellow,c2=,c3=black", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right("Id"::text,2)||'='||coalesce("Color",''),',' ORDER BY "Id") FROM "BoardItem"
            """));
        await ExecuteAsync(schema.ConnectionString, """UPDATE "BoardItem" SET "Color"='blue-4' WHERE "Id"='00000000-0000-0000-0000-0000000000c2'""");
        await ExecuteAsync(schema.ConnectionString, """UPDATE "BoardItem" SET "Color"='slate-1' WHERE "Id"='00000000-0000-0000-0000-0000000000c3'""");
        foreach (var refused in new[] { "blue-6", "coral", "Blue-2", "brown" })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
                $"""UPDATE "BoardItem" SET "Color"='{refused}' WHERE "Id"='00000000-0000-0000-0000-0000000000c1'"""));
            Assert.Equal("CK_BoardItem_Color", failure.ConstraintName);
        }
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
