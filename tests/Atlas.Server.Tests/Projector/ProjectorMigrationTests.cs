using System.Text.Json.Nodes;
using Npgsql;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>Migration 2026102800 over the rows an installation already has: requirement blocks saved before it (pages written by an
/// editor with the plugin) are indexed for the working and the published revision, and the catalog table follows its page.</summary>
public sealed class ProjectorMigrationTests
{
    private const long ProjectorVersion = 2026102800;

    /// <summary>Migration 2026110870 dropped the index triggers the tests of the earlier migrations exercise: they stop before it.</summary>
    private const long IndexInCode = 2026110870;

    [PostgreSqlFact]
    public async Task Existing_pages_are_indexed_and_the_index_follows_later_saves_and_publications()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, ProjectorVersion);
        var published = Json(Block("b1", "portal.ssl", "SSL", state: "done", direction: "inbound"));
        var working = Json(Paragraph("Úvod"), Block("b1", "portal.ssl", "SSL", state: "planned", direction: "inbound",
            content: [Block("b2", "portal.dns", "DNS", requires: "portal.ssl", party: "IT KBN")]), Summary("s"));
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',2);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-0000000000f1',1,'KBN','{published}','','00000000-0000-0000-0000-000000000001',now()-interval '1 day',now(),'web'),
              ('00000000-0000-0000-0000-0000000000e2','00000000-0000-0000-0000-0000000000f1',2,'KBN','{working}','','00000000-0000-0000-0000-000000000001',now(),NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e1';
            """);

        await ApplyRangeAsync(schema.ConnectionString, ProjectorVersion, IndexInCode);

        Assert.Equal("e1:1:b1:portal.ssl:SSL:done:inbound:f:t:|e2:1:b1:portal.ssl:SSL:planned:inbound:t:f:|e2:2:b2:portal.dns:DNS:planned:internal:t:f:b1",
            await IndexAsync(schema.ConnectionString));
        Assert.Equal("portal.ssl|IT KBN", await ScalarAsync(schema.ConnectionString,
            """SELECT "Requires" || '|' || "Party" FROM "ProjectorRequirementIndex" WHERE "BlockId"='b2'"""));

        // A save closes the working revision and adds one; publishing it moves the published rows; the page's own rows only.
        await ExecuteAsync(schema.ConnectionString, $"""
            BEGIN;
            UPDATE "PageRevision" SET "ActiveTo"=now() WHERE "Id"='00000000-0000-0000-0000-0000000000e2';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e3','00000000-0000-0000-0000-0000000000f1',3,'KBN','{Json(Paragraph("Bez požadavků"))}','','00000000-0000-0000-0000-000000000001',now()+interval '1 second','web');
            COMMIT;
            """);
        Assert.Equal("e1:1:b1:portal.ssl:SSL:done:inbound:f:t:", await IndexAsync(schema.ConnectionString));
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e3'""");
        Assert.Equal("", await IndexAsync(schema.ConnectionString));

        // A catalog mark and index rows belong to their page and go with it.
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "ProjectorCatalogPage"("PageId","MarkedById","MarkedAt")
              VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-000000000001',now());
            """);
        Assert.Equal("c|c", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(confdeltype::text,'|' ORDER BY conname) FROM pg_constraint
            WHERE conname IN ('FK_ProjectorCatalogPage_Page','FK_ProjectorRequirementIndex_Page')
            """));
    }

    /// <summary>The incremental refresh over pages that existed before the migration leaves what a full rebuild would, after a save,
    /// a publication and a content change in place, and remembers the revisions it read, those without requirements included.</summary>
    [PostgreSqlFact]
    public async Task The_incremental_refresh_matches_a_full_rebuild_and_remembers_what_it_read()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_incremental_");
        await ApplyBeforeAsync(schema.ConnectionString, ProjectorVersion);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',2);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-0000000000f1',1,'KBN','{Json(Block("b1", "portal.ssl", "SSL", state: "done"))}','','00000000-0000-0000-0000-000000000001',now()-interval '1 day',now(),'web'),
              ('00000000-0000-0000-0000-0000000000e2','00000000-0000-0000-0000-0000000000f1',2,'KBN','{Json(Block("b1", "portal.ssl", "SSL"), Block("b2", "portal.dns", "DNS"))}','','00000000-0000-0000-0000-000000000001',now(),NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e1';
            """);

        await ApplyRangeAsync(schema.ConnectionString, ProjectorVersion, IndexInCode);

        Assert.Equal("e1:1:b1:portal.ssl:SSL:done:internal:f:t:|e2:1:b1:portal.ssl:SSL:planned:internal:t:f:|e2:2:b2:portal.dns:DNS:planned:internal:t:f:",
            await IndexAsync(schema.ConnectionString));
        // A save: the closed working revision's rows go (not published), the new one is walked.
        await ExecuteAsync(schema.ConnectionString, $"""
            BEGIN;
            UPDATE "PageRevision" SET "ActiveTo"=now() WHERE "Id"='00000000-0000-0000-0000-0000000000e2';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e3','00000000-0000-0000-0000-0000000000f1',3,'KBN','{Json(Block("b2", "portal.dns", "DNS", state: "blocked"))}','','00000000-0000-0000-0000-000000000001',now()+interval '1 second','web');
            COMMIT;
            """);
        Assert.Equal("e1:1:b1:portal.ssl:SSL:done:internal:f:t:|e3:1:b2:portal.dns:DNS:blocked:internal:t:f:", await IndexAsync(schema.ConnectionString));
        // Publishing the working revision moves the flags; the old publication's rows go.
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e3'""");
        Assert.Equal("e3:1:b2:portal.dns:DNS:blocked:internal:t:t:", await IndexAsync(schema.ConnectionString));
        // Content changed in place (import, source replace) is walked again.
        await ExecuteAsync(schema.ConnectionString, $"""
            UPDATE "PageRevision" SET "ContentJson"='{Json(Block("b2", "portal.dns", "DNS", state: "done"), Block("b4", "portal.sms", "SMS"))}' WHERE "Id"='00000000-0000-0000-0000-0000000000e3'
            """);
        var incremental = await IndexAsync(schema.ConnectionString);
        Assert.Equal("e3:1:b2:portal.dns:DNS:done:internal:t:t:|e3:2:b4:portal.sms:SMS:planned:internal:t:t:", incremental);
        await ExecuteAsync(schema.ConnectionString, "SELECT atlas_rebuild_projector_requirements()");
        Assert.Equal(incremental, await IndexAsync(schema.ConnectionString));
        // A save without requirements is read once and remembered, so the next firing does not detoast it again.
        await ExecuteAsync(schema.ConnectionString, $"""
            BEGIN;
            UPDATE "PageRevision" SET "ActiveTo"=now()+interval '1 second' WHERE "Id"='00000000-0000-0000-0000-0000000000e3';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e4','00000000-0000-0000-0000-0000000000f1',4,'KBN','{Json(Paragraph("Bez požadavků"))}','','00000000-0000-0000-0000-000000000001',now()+interval '2 seconds','web');
            COMMIT;
            """);
        Assert.Equal("e3|e4", await ScalarAsync(schema.ConnectionString,
            """SELECT string_agg(right("RevisionId"::text,2),'|' ORDER BY "RevisionId") FROM "ProjectorIndexedRevision" """));
    }

    /// <summary>Migration 2026103160 over pages saved before it: the index rows get the party's contact.</summary>
    [PostgreSqlFact]
    public async Task Existing_revisions_give_the_index_its_contact()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_links_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026103160);
        var contact = "00000000-0000-0000-0000-0000000000c1";
        var linked = Block("b2", "portal.dns", "DNS", party: "Jan");
        linked["attrs"]!["partyContactId"] = contact;
        await SeedPageAsync(schema.ConnectionString, linked);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal($"b1:|b2:{contact}", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg("BlockId" || ':' || "PartyContactId",'|' ORDER BY "Position") FROM "ProjectorRequirementIndex" WHERE "IsWorking"
            """));
    }

    /// <summary>Migration 2026110690 over an installation with the change log of 2026103160: the log, its writers and the fingerprint go,
    /// and the index keeps its rows and still follows saves, publications and a rebuild.</summary>
    [PostgreSqlFact]
    public async Task Dropping_the_change_log_keeps_the_index_and_its_triggers()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_history_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110690);
        await SeedPageAsync(schema.ConnectionString, Block("b2", "portal.dns", "DNS", party: "Jan"));
        Assert.True((long)(await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ProjectorRequirementChange" """))! > 0);
        var before = await IndexAsync(schema.ConnectionString);

        await ApplyRangeAsync(schema.ConnectionString, 2026110690, IndexInCode);

        Assert.Equal(before, await IndexAsync(schema.ConnectionString));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_record_projector_publication','atlas_backfill_projector_history','atlas_projector_fingerprint')
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM pg_class WHERE relname='ProjectorRequirementChange'"""));
        // A save and a publication after the migration are indexed by the triggers, and a rebuild after a restore reads the same.
        await ExecuteAsync(schema.ConnectionString, $"""
            BEGIN;
            UPDATE "PageRevision" SET "ActiveTo"='2026-10-05' WHERE "Id"='00000000-0000-0000-0000-0000000000e3';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e4','00000000-0000-0000-0000-0000000000f1',4,'KBN','{Json(Block("b1", "portal.ssl", "SSL", state: "done"), Block("b2", "portal.dns", "DNS", party: "Jan", state: "in_progress"))}','','00000000-0000-0000-0000-000000000002','2026-10-05','web');
            COMMIT;
            """);
        Assert.Equal("e3:1:b1:portal.ssl:SSL:done:internal:f:t:|e3:2:b2:portal.dns:DNS:planned:internal:f:t:|e4:1:b1:portal.ssl:SSL:done:internal:t:f:|e4:2:b2:portal.dns:DNS:in_progress:internal:t:f:",
            await IndexAsync(schema.ConnectionString));
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e4'""");
        var published = await IndexAsync(schema.ConnectionString);
        Assert.Equal("e4:1:b1:portal.ssl:SSL:done:internal:t:t:|e4:2:b2:portal.dns:DNS:in_progress:internal:t:t:", published);
        await ExecuteAsync(schema.ConnectionString, "SELECT atlas_rebuild_projector_requirements()");
        Assert.Equal(published, await IndexAsync(schema.ConnectionString));
    }

    /// <summary>A page of two authors: three saved revisions (b1 planned, done; <paramref name="second"/> added in e3) and two publications.</summary>
    private static Task SeedPageAsync(string connectionString, JsonNode second) => ExecuteAsync(connectionString, $"""
        INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
          ('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp'),
          ('00000000-0000-0000-0000-000000000002','editor','Editor',false,true,'stamp');
        INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
        INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
          VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',3);
        INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
          ('00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-0000000000f1',1,'KBN','{Json(Block("b1", "portal.ssl", "SSL"))}','','00000000-0000-0000-0000-000000000001','2026-10-01','2026-10-02','web'),
          ('00000000-0000-0000-0000-0000000000e2','00000000-0000-0000-0000-0000000000f1',2,'KBN','{Json(Block("b1", "portal.ssl", "SSL", state: "done"))}','','00000000-0000-0000-0000-000000000002','2026-10-02','2026-10-03','web'),
          ('00000000-0000-0000-0000-0000000000e3','00000000-0000-0000-0000-0000000000f1',3,'KBN','{Json(Block("b1", "portal.ssl", "SSL", state: "done"), second)}','','00000000-0000-0000-0000-000000000001','2026-10-03',NULL,'web');
        INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","ActiveTo","Channel") VALUES
          (gen_random_uuid(),'00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-0000000000e1','00000000-0000-0000-0000-000000000001','2026-10-01 12:00','2026-10-04','web'),
          (gen_random_uuid(),'00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-0000000000e3','00000000-0000-0000-0000-000000000001','2026-10-04',NULL,'web');
        UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e3';
        """);

    /// <summary>Migration 2026103196: the existing blocks that require another page's are the rows of the cross-page index.</summary>
    [PostgreSqlFact]
    public async Task Existing_cross_page_requirements_are_read_by_their_partial_index()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_cross_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026103196);
        const string Source = "00000000-0000-0000-0000-0000000000f1", Page = "00000000-0000-0000-0000-0000000000f2";
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('00000000-0000-0000-0000-000000000001','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('{Source}','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1),
              ('{Page}','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e1','{Source}',1,'Infrastruktura','{Json(Block("b1", "vpn", "VPN"))}','','00000000-0000-0000-0000-000000000001','2026-10-01','web'),
              ('00000000-0000-0000-0000-0000000000e2','{Page}',1,'KBN','{Json(Block("b2", "portal.ssl", "SSL", requires: "vpn@" + Source), Block("b3", "portal.dns", "DNS", requires: "portal.ssl"))}','','00000000-0000-0000-0000-000000000001','2026-10-01','web');
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id";
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.EndsWith("""USING btree ("PageId") WHERE ("Requires" ~~ '%@%'::text)""",
            (string?)await ScalarAsync(schema.ConnectionString, """SELECT indexdef FROM pg_indexes WHERE indexname='IX_ProjectorRequirementIndex_CrossPage'"""));
        Assert.Equal("b2", await ScalarAsync(schema.ConnectionString, """SELECT string_agg("BlockId",',') FROM "ProjectorRequirementIndex" WHERE "Requires" LIKE '%@%'"""));
    }

    /// <summary>Migration 2026110820 over existing rows: the blocks keep their rows, a revision that already holds requirement headings
    /// (written raw, as an import could) is indexed with them, the triggers index later saves of either shape with the page's own links
    /// relative to it, a rebuild reads the same, and the rows requiring another page's heading are read by their partial index.</summary>
    [PostgreSqlFact]
    public async Task Requirement_headings_are_indexed_beside_the_blocks()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_headings_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110820);
        const string Page = "00000000-0000-0000-0000-0000000000f1", Other = "00000000-0000-0000-0000-0000000000f2";
        var headings = Json(Heading("Portál", "portal"), RequirementHeading("portal.ssl", "SSL", "waiting_for_input"),
            Paragraph("od IT KBN · do 15. 11. 2026"), RequirementHeading("portal.dns", "DNS", "planned"),
            Dependencies((Guid.Parse(Page), "portal.ssl"), (Guid.Parse(Other), "infra.vpn")));
        await SeedPageAsync(schema.ConnectionString, Block("b2", "portal.dns", "DNS", party: "Jan"));
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('{Other}','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e9','{Other}',1,'Infra','{headings}','','00000000-0000-0000-0000-000000000001','2026-10-01','web');
            """);
        var before = await IndexAsync(schema.ConnectionString);

        await ApplyRangeAsync(schema.ConnectionString, 2026110820, IndexInCode);

        // The other page's revision holds headings: its link to itself is local, the one to the first page is pageId#headingId.
        Assert.Equal(before + "|e9:1:portal.ssl:portal.ssl:SSL:waiting_for_input:internal:t:f:|e9:2:portal.dns:portal.dns:DNS:planned:internal:t:f:",
            await IndexAsync(schema.ConnectionString));
        Assert.Equal($"2026-11-15|IT KBN|", await ScalarAsync(schema.ConnectionString,
            """SELECT "Date" || '|' || "Party" || '|' || "Requires" FROM "ProjectorRequirementIndex" WHERE "RevisionId"='00000000-0000-0000-0000-0000000000e9' AND "Position"=1"""));
        Assert.Equal($"{Page}#portal.ssl,infra.vpn", await ScalarAsync(schema.ConnectionString,
            """SELECT "Requires" FROM "ProjectorRequirementIndex" WHERE "RevisionId"='00000000-0000-0000-0000-0000000000e9' AND "Position"=2"""));

        // A save of the first page in the new shape: its rows follow, its link to itself is local, the one to the other page is pageId#id.
        await ExecuteAsync(schema.ConnectionString, $"""
            BEGIN;
            UPDATE "PageRevision" SET "ActiveTo"='2026-10-05' WHERE "Id"='00000000-0000-0000-0000-0000000000e3';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e4','{Page}',4,'KBN','{headings}','','00000000-0000-0000-0000-000000000002','2026-10-05','web');
            COMMIT;
            """);
        Assert.Equal($"portal.ssl,{Other}#infra.vpn", await ScalarAsync(schema.ConnectionString,
            """SELECT "Requires" FROM "ProjectorRequirementIndex" WHERE "RevisionId"='00000000-0000-0000-0000-0000000000e4' AND "Position"=2"""));
        var saved = await IndexAsync(schema.ConnectionString);
        await ExecuteAsync(schema.ConnectionString, "SELECT atlas_rebuild_projector_requirements()");
        Assert.Equal(saved, await IndexAsync(schema.ConnectionString));
        Assert.Equal("portal.dns", await ScalarAsync(schema.ConnectionString,
            """SELECT string_agg("BlockId",',') FROM "ProjectorRequirementIndex" WHERE "Requires" LIKE '%#%' AND "PageId"='00000000-0000-0000-0000-0000000000f1'"""));
        Assert.EndsWith("""USING btree ("PageId") WHERE ("Requires" ~~ '%#%'::text)""", (string?)await ScalarAsync(schema.ConnectionString,
            """SELECT indexdef FROM pg_indexes WHERE indexname='IX_ProjectorRequirementIndex_CrossPageHeading'"""));
    }

    /// <summary>Migration 2026110840 over existing rows: a revision indexed by 2026110820's first-in-section rule (the lines of a removed
    /// heading's requirement read as the requirement's above) is rebuilt with the fixed places, and later saves read them too.</summary>
    [PostgreSqlFact]
    public async Task Requirement_heading_lines_are_read_at_their_fixed_places()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_places_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110840);
        const string Page = "00000000-0000-0000-0000-0000000000f1", Other = "00000000-0000-0000-0000-0000000000f2";
        var joined = Json(RequirementHeading("prvni", "První", "in_progress"), Paragraph("Poznámka prvního"),
            Dependencies((Guid.Parse(Page), "cil")), Paragraph("pro Petr · do 1. 1. 2027"),
            RequirementHeading("treti", "Třetí"), Dependencies((Guid.Parse(Page), "cil")), Paragraph("od Eva · do 2. 1. 2027"));
        await SeedPageAsync(schema.ConnectionString, Block("b2", "portal.dns", "DNS"));
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('{Other}','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e9','{Other}',1,'Infra','{joined}','','00000000-0000-0000-0000-000000000001','2026-10-01','web');
            """);
        const string Rows = """
            SELECT string_agg("BlockId" || '|' || "Requires" || '|' || "Party" || '|' || "Date", ';' ORDER BY "Position")
            FROM "ProjectorRequirementIndex" WHERE "RevisionId"='00000000-0000-0000-0000-0000000000e9'
            """;
        Assert.Equal($"prvni|{Page}#cil|Petr|2027-01-01;treti|{Page}#cil|Eva|2027-01-02", await ScalarAsync(schema.ConnectionString, Rows));

        await ApplyRangeAsync(schema.ConnectionString, 2026110840, IndexInCode);

        Assert.Equal($"prvni|||;treti|{Page}#cil|Eva|2027-01-02", await ScalarAsync(schema.ConnectionString, Rows));
        var rebuilt = await IndexAsync(schema.ConnectionString);
        await ExecuteAsync(schema.ConnectionString, "SELECT atlas_rebuild_projector_requirements()");
        Assert.Equal(rebuilt, await IndexAsync(schema.ConnectionString));
    }

    /// <summary>Migration 2026110870 over rows the triggers indexed: the triggers and the SQL functions go, the rows stay, the full rebuild is
    /// queued, and the index in code (ProjectorIndex) rebuilds exactly the rows the SQL read, for requirement blocks (nested, a party's
    /// contact, cross-page requires) and requirement headings (facts and dependency lines) alike; a save afterwards is indexed when a read
    /// makes the page current, not by the save.</summary>
    [PostgreSqlFact]
    public async Task The_index_moves_to_code_with_the_rows_the_sql_read()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_projector_in_code_");
        await ApplyBeforeAsync(schema.ConnectionString, IndexInCode);
        const string Page = "00000000-0000-0000-0000-0000000000f1", Other = "00000000-0000-0000-0000-0000000000f2";
        var linked = Block("b2", "portal.dns", "DNS", party: "Jan", requires: "portal.ssl,infra.vpn@" + Other,
            content: [Block("b5", "portal.sms", "SMS", state: "blocked")]);
        linked["attrs"]!["partyContactId"] = "00000000-0000-0000-0000-0000000000c1";
        await SeedPageAsync(schema.ConnectionString, linked);
        // The deadline line alone: the SQL read the party of a version-3 facts line, the code reads it from the heading (shape §6).
        var headings = Json(Heading("Portál", "portal"), RequirementHeading("portal.ssl", "SSL", "waiting_for_input"),
            Paragraph("do 15. 11. 2026"), RequirementHeading("portal.dns", "DNS", "planned"),
            Dependencies((Guid.Parse(Page), "portal.ssl"), (Guid.Parse(Other), "infra.vpn")));
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('{Other}','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e9','{Other}',1,'Infra','{headings}','','00000000-0000-0000-0000-000000000001','2026-10-01','web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000e9' WHERE "Id"='{Other}';
            """);
        var sql = await FullIndexAsync(schema.ConnectionString);
        Assert.Contains("b5", sql, StringComparison.Ordinal);
        Assert.Contains("portal.ssl", sql, StringComparison.Ordinal);

        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);

        Assert.Equal(sql, await FullIndexAsync(host.ConnectionString));
        Assert.Equal(0L, await ScalarAsync(host.ConnectionString, """
            SELECT count(*) FROM pg_proc WHERE proname LIKE 'atlas_projector%' OR proname LIKE 'atlas_%projector_requirements%'
            """));
        Assert.Equal(0L, await ScalarAsync(host.ConnectionString, """SELECT count(*) FROM pg_trigger WHERE tgname LIKE '%ProjectorRequirementIndex%'"""));
        Assert.Equal(1L, await ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun" WHERE "JobId"='{Atlas.Server.Features.Projector.ProjectorIndexJob.JobId}' AND "State"='queued'
            """));
        // The full rebuild in code reads exactly what the SQL read.
        await ExecuteAsync(host.ConnectionString, """DELETE FROM "ProjectorRequirementIndex"; DELETE FROM "ProjectorIndexedRevision";""");
        await host.Services.GetRequiredService<Atlas.Server.Platform.Jobs.IJobRunner>().RunToEndAsync(Atlas.Server.Features.Projector.ProjectorIndexJob.JobId, "Test");
        Assert.Equal(sql, await FullIndexAsync(host.ConnectionString));

        // A save no longer touches the index; the next read makes the page current.
        await ExecuteAsync(host.ConnectionString, $"""
            BEGIN;
            UPDATE "PageRevision" SET "ActiveTo"='2026-10-05' WHERE "Id"='00000000-0000-0000-0000-0000000000e3';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000e4','{Page}',4,'KBN','{headings}','','00000000-0000-0000-0000-000000000002','2026-10-05','web');
            COMMIT;
            """);
        Assert.Equal(sql, await FullIndexAsync(host.ConnectionString));
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, [Guid.Parse(Page)], default);
            await transaction.CommitAsync();
        }
        Assert.Equal($"portal.ssl,{Other}#infra.vpn", await ScalarAsync(host.ConnectionString,
            """SELECT "Requires" FROM "ProjectorRequirementIndex" WHERE "RevisionId"='00000000-0000-0000-0000-0000000000e4' AND "Position"=2"""));
        Assert.Equal("e3|e4|e9", await ScalarAsync(host.ConnectionString,
            """SELECT string_agg(right("RevisionId"::text,2),'|' ORDER BY "RevisionId") FROM "ProjectorIndexedRevision" """));
    }

    private static async Task<string> FullIndexAsync(string connectionString)
    {
        return (string)(await ScalarAsync(connectionString, """
            SELECT coalesce(string_agg(concat_ws(':', "PageId", "RevisionId", "Position", "IsWorking", "IsPublished", "BlockId", "Key", "Title", "Direction",
              "State", "Date", "Party", "Where", "Requires", coalesce("ParentBlockId",'-'), "PartyContactId"), E'\n' ORDER BY "RevisionId","Position"),'')
            FROM "ProjectorRequirementIndex"
            """))!;
    }

    private static async Task<string> IndexAsync(string connectionString)
    {
        return (string)(await ScalarAsync(connectionString, """
            SELECT coalesce(string_agg(right("RevisionId"::text,2) || ':' || "Position" || ':' || "BlockId" || ':' || "Key" || ':' || "Title" || ':'
              || "State" || ':' || "Direction" || ':' || "IsWorking"::text::char || ':' || "IsPublished"::text::char || ':' || coalesce("ParentBlockId",''),
              '|' ORDER BY "RevisionId","Position"),'')
            FROM "ProjectorRequirementIndex"
            """))!;
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

    /// <summary>Applies the embedded scripts from <paramref name="from"/> up to below <paramref name="before"/>, with their journal.</summary>
    private static async Task ApplyRangeAsync(string connectionString, long from, long before)
    {
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version >= from && item.Version < before).OrderBy(item => item.Version))
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
