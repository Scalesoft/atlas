using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

/// <summary>
/// Migration 2026110930 (no logic in the database, S3) over an installation its triggers kept: what they wrote stays as it was (the
/// envelope, the tree's history and token, a space's metadata history, the contributions, the version groups), the triggers and their
/// functions are gone, and bare SQL derives nothing any more; a page it inserts without a place takes 1024. No rebuild job: the rows
/// are consistent up to the migration, and from it on C# writes them (ContentRowEffects).
/// </summary>
public sealed class PageHousekeepingTriggersMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
    private const string Page = "00000000-0000-0000-0000-0000000000a1", Board = "00000000-0000-0000-0000-0000000000b1";

    private static readonly string[] Triggers = ["TR_PageRevision_Contribution", "TR_Page_ContentResource", "TR_Board_ContentResource", "TR_Page_Changed",
        "TR_PageRevision_Changed", "TR_PageRevision_Group", "TR_Page_PrepareOrganization", "TR_Page_OrganizationHistory", "TR_Board_Organization",
        "TR_TreeReference_Organization", "TR_Space_Organization", "TR_Space_MetadataHistory"];

    private static readonly string[] Functions = ["atlas_track_page_contribution", "atlas_track_content_resource", "atlas_notify_page_changed",
        "atlas_page_revision_group", "atlas_prepare_page_organization", "atlas_version_page_organization", "atlas_version_board_organization",
        "atlas_version_tree_reference_organization", "atlas_initialize_space_organization", "atlas_version_space_metadata"];

    [PostgreSqlFact]
    public async Task Migration_2026110930_drops_the_triggers_and_keeps_what_they_wrote()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_housekeeping_");
        var db = schema.ConnectionString;
        await OutboxTests.ApplyBeforeAsync(db, 2026110930);
        await OutboxTests.ExecuteAsync(db, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            UPDATE "Space" SET "Name"='Renamed' WHERE "Id"='{Space}';
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Space}','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('00000000-0000-0000-0000-0000000000c1','{Page}',1,'Old','{"{"}{"}"}','old','{Owner}',now(),'web');
            INSERT INTO "Board"("Id","SpaceId","SortOrder","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed")
              VALUES('{Board}','{Space}',2048,'Tabule',3,now(),'{Owner}',now(),false);
            INSERT INTO "TreeReference"("Id","SpaceId","SortOrder","TargetId","CreatedBy","CreatedAt") VALUES(gen_random_uuid(),'{Space}',4096,'{Board}','{Owner}',now());
            """);
        var kept = await DerivedAsync(db);
        Assert.Equal(
            "atlas.board:3:Tabule;atlas.page:1: | 1024:1 | 4 | 2 | 1 | 00000000-0000-0000-0000-0000000000c1", kept);

        await OutboxTests.ApplyBeforeAsync(db, 2026110931, from: 2026110930);

        Assert.Equal(kept, await DerivedAsync(db));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(db,
            $"SELECT count(*) FROM pg_trigger WHERE tgname IN ({string.Join(',', Triggers.Select(name => $"'{name}'"))})"));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(db,
            $"SELECT count(*) FROM pg_proc WHERE proname IN ({string.Join(',', Functions.Select(name => $"'{name}'"))})"));
        // Bare SQL derives nothing now: no envelope, history, contribution or token; a page without a place takes 1024.
        await OutboxTests.ExecuteAsync(db, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('00000000-0000-0000-0000-0000000000a2','{Space}','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000a2',1,'New','{"{"}{"}"}','new','{Owner}',now(),'web');
            """);
        Assert.Equal(kept, await DerivedAsync(db));
        Assert.Equal(1024, await OutboxTests.ScalarAsync(db, """SELECT "SortOrder" FROM "Page" WHERE "Id"='00000000-0000-0000-0000-0000000000a2'"""));
        Assert.Equal(DBNull.Value, await OutboxTests.ScalarAsync(db, """SELECT "GroupId" FROM "PageRevision" WHERE "Id"='00000000-0000-0000-0000-0000000000c2'"""));
    }

    /// <summary>The rows the triggers derived: envelopes, the first page's place and history, the tree token, the metadata history,
    /// the contributions and the version group of the revision.</summary>
    private static async Task<string> DerivedAsync(string db)
    {
        return (string)(await OutboxTests.ScalarAsync(db, $"""
            SELECT (SELECT string_agg("TypeId" || ':' || "Version" || ':' || coalesce("Title",''), ';' ORDER BY "TypeId") FROM "ContentResource")
              || ' | ' || (SELECT "SortOrder" || ':' || (SELECT count(*) FROM "PageOrganizationRevision") FROM "Page" WHERE "Id"='{Page}')
              || ' | ' || (SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId"='{Space}')
              || ' | ' || (SELECT count(*) FROM "SpaceMetadataRevision")
              || ' | ' || (SELECT count(*) FROM "PageContribution")
              || ' | ' || (SELECT string_agg(coalesce("GroupId"::text,'-'), ',') FROM "PageRevision" WHERE "Id"='00000000-0000-0000-0000-0000000000c1')
            """))!;
    }
}
