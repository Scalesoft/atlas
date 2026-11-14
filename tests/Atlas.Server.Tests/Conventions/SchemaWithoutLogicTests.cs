using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Conventions;

/// <summary>
/// No logic in the database (3.6, plan 2026-11-04): after every migration the database holds no user-defined trigger, function or
/// procedure; the logic is C#. Functions of extensions (pgvector's operators and casts) are the extension's, not ours. A migration
/// that creates one fails here (the last migration that dropped the leftovers is 2026110990).
/// </summary>
[Trait("Category", "Guard")]
public sealed class SchemaWithoutLogicTests
{
    /// <summary>Every user-defined function, procedure and trigger of the database, outside the system schemas and the extensions.</summary>
    internal const string Logic = """
        SELECT string_agg(item, ', ' ORDER BY item) FROM (
          SELECT 'function ' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' AS item
          FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
          WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
            AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
          UNION ALL
          SELECT 'trigger ' || c.relname || '.' || t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE NOT t.tgisinternal
        ) logic
        """;

    [PostgreSqlFact]
    public async Task The_migrated_schema_has_no_triggers_functions_or_procedures()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_no_logic_");
        var logic = await OutboxTests.ScalarAsync(schema.ConnectionString, Logic) as string;
        Assert.True(logic is null, "Logic in the database (drop it in a migration, write it in C#): " + logic);
    }

    /// <summary>Migration 2026110990 over an installation with a space switched out of AI and one left in: atlas_space_in_ai, which
    /// 2026110946 kept for the index's functions, goes, the settings stay as they were, AccessSql.SpaceInAi answers what the function
    /// did, and nothing of ours is left.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110990_drops_the_last_function_and_keeps_the_settings()
    {
        const string Owner = "00000000-0000-0000-0000-000000000001", Out = "00000000-0000-0000-0000-00000000000a", In = "00000000-0000-0000-0000-00000000000b";
        await using var schema = await DisposableSchema.CreateAsync("atlas_no_logic_990_");
        var db = schema.ConnectionString;
        await OutboxTests.ApplyBeforeAsync(db, 2026110990);
        await OutboxTests.ExecuteAsync(db, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Out}','Mimo','book-open','red'),('{In}','Uvnitř','book-open','blue');
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES(gen_random_uuid(),'{Out}',false,'{Owner}',now());
            """);
        const string Settings = """SELECT string_agg("SpaceId" || ':' || "Included", ',' ORDER BY "SpaceId") FROM "SpaceAiSetting" """;
        var settings = await OutboxTests.ScalarAsync(db, Settings);
        Assert.Equal("false,true", await OutboxTests.ScalarAsync(db,
            $"""SELECT atlas_space_in_ai('{Out}') || ',' || atlas_space_in_ai('{In}')"""));

        // "Before 2026110991" is exactly migration 2026110990 (the next one is 2026110995).
        await OutboxTests.ApplyBeforeAsync(db, 2026110991, from: 2026110990);

        Assert.Null(await OutboxTests.ScalarAsync(db, Logic) as string);
        Assert.Equal(settings, await OutboxTests.ScalarAsync(db, Settings));
        Assert.Equal("false,true", await OutboxTests.ScalarAsync(db, $"""
            SELECT bool_and(x.included) FILTER (WHERE x.id = '{Out}') || ',' || bool_and(x.included) FILTER (WHERE x.id = '{In}')
            FROM (SELECT s."Id" AS id, {Atlas.Server.Platform.Identity.AccessSql.SpaceInAi("s.\"Id\"")} AS included FROM "Space" s) x
            """));
    }
}
