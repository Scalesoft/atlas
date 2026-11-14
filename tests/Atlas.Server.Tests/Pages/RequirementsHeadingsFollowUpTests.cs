using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Validation;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Pages.RequirementHeadingConverterTests;

namespace Atlas.Server.Tests.Pages;

/// <summary>The conversion's follow-up run (a page with live activity waits for its editors) on a durable host, where the scheduler
/// really delivers: it is queued with its time AND scheduled, and it starts by itself when its time comes.</summary>
public sealed class RequirementsHeadingsFollowUpTests
{
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
        Live = "00000000-0000-0000-0000-0000000000a4", LiveWorking = "00000000-0000-0000-0000-0000000000b5";

    [PostgreSqlFact]
    public async Task A_page_with_unsaved_live_edits_is_converted_by_the_follow_up_run_when_its_time_comes()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_followup_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110700);
        var json = Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e631", "zive", "Rozepsaný požadavek"));
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Live}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{LiveWorking}','{Live}',1,'Živá','{json}','živá','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',2,1,1, now());
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled, durable: true);
        // The run the migration queued starts with the host and defers the page: a follow-up is queued for later.
        await WaitAsync(host, """SELECT count(*) FROM "JobRun" WHERE "JobId"='pages.requirements-headings' AND "State"='queued' AND "NotBefore" > now()""", 1);
        Assert.True((long)(await OutboxTests.ScalarAsync(host.ConnectionString, """
            SELECT count(*) FROM wolverine_incoming_envelopes WHERE message_type LIKE '%RunJob' AND status = 'Scheduled'
              AND position('pages.requirements-headings'::bytea IN body) > 0
            """))! >= 1, "The follow-up run has no scheduled message.");
        // Its time comes: the page's edits are saved by then, the scheduled message fires, and the run converts the page.
        await OutboxTests.ExecuteAsync(host.ConnectionString, """
            UPDATE "PageCollaboration" SET "LastSavedSequence" = "Sequence", "UpdatedAt" = now() - interval '1 hour';
            UPDATE "JobRun" SET "NotBefore" = now() + interval '3 seconds' WHERE "JobId"='pages.requirements-headings' AND "State"='queued';
            UPDATE wolverine_incoming_envelopes SET execution_time = now() + interval '3 seconds' WHERE message_type LIKE '%RunJob' AND status = 'Scheduled';
            """);
        await WaitAsync(host, """SELECT count(*) FROM "JobRun" WHERE "JobId"='pages.requirements-headings' AND "State"='succeeded'""", 2);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT count(*) FROM "PageRevision" WHERE "PageId"='{Live}' AND strpos("ContentJson",'"type":"requirement"')>0"""));
    }

    /// <summary>A reader's open touches the page's collaboration row (a reset to the saved revision stamps it): that is no editing. A page
    /// with everything saved converts on that very open and in the job, however recent the row; only unsaved edits keep a page as stored.</summary>
    [PostgreSqlFact]
    public async Task An_open_that_just_touched_the_collaboration_row_does_not_defer_the_conversion()
    {
        const string Other = "00000000-0000-0000-0000-0000000000a5", OtherWorking = "00000000-0000-0000-0000-0000000000b6";
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_touch_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110700);
        var json = Envelope(Heading(2, "h-sit", "Síť"), Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e631", "portal.vpn", "VPN tunel", state: "done", body: [Paragraph("Hotovo")]),
            Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e632", "portal.dns", "DNS záznam", direction: "inbound", party: "Správa sítě", date: "2026-11-20", requires: "portal.vpn"));
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Live}','{Space}','{Writer}',1), ('{Other}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{LiveWorking}','{Live}',1,'Zakázka','{json}','a','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{OtherWorking}','{Other}',1,'Druhá','{json}','b','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',0,0,0, now()),
              ('{Other}','00000000-0000-0000-0000-0000000000c3','{OtherWorking}',0,0,0, now());
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""DELETE FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}'""");
        host.Actor.Current = new UserAccount { Id = Guid.Parse(Writer), Username = "writer", DisplayName = "Writer", IsWikiAdmin = true, IsActive = true, SecurityStamp = "stamp" };

        var opened = await host.InvokeAsync<PageDto>(new GetPage(Guid.Parse(Live)));
        Assert.DoesNotContain("\"type\":\"requirement\"", opened.ContentJson, StringComparison.Ordinal);

        var run = await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        Assert.Contains("\"deferred\": 0", (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT CAST("Summary" AS text) FROM "JobRun" WHERE "Id" = {run}"""))!);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "PageRevision" WHERE strpos("ContentJson",'"type":"requirement"')>0"""));
    }

    /// <summary>Owner 2026-11-10 („převést vše po prvním startu 4.0“): pages whose live documents hold edits nobody saved (typed in 3.5,
    /// then the server was upgraded: nobody connected, nothing in memory) are not deferred. The job stores the unsaved edits as their
    /// author's revision first (the server's own checkpoint) and converts each page with them in the same run; two such pages in a row,
    /// as the first one's conversion on its save's read must not make the batch skip the second one's checkpoint.</summary>
    [PostgreSqlFact]
    public async Task Unsaved_live_edits_with_nobody_connected_are_saved_by_the_job_and_converted_with_the_page()
    {
        const string Second = "00000000-0000-0000-0000-0000000000a5", SecondWorking = "00000000-0000-0000-0000-0000000000b6";
        var pages = new[] { (Page: Live, Working: LiveWorking, Generation: "00000000-0000-0000-0000-0000000000c2", Block: "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e631"),
            (Page: Second, Working: SecondWorking, Generation: "00000000-0000-0000-0000-0000000000c3", Block: "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e632") };
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_requirements_unsaved_");
        await using var before = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        await OutboxTests.ExecuteAsync(before.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            """);
        var host = CollaborationSchema.KnownHostSchemas[^1];
        var authority = before.Services.GetRequiredService<ILiveWriteAuthority>();
        var writer = before.Services.GetRequiredService<LiveDocumentWriter>();
        foreach (var (page, working, generation, block) in pages)
        {
            var json = Envelope(Block(block, "zive", "Rozepsaný požadavek"), Paragraph("Text"));
            using var stored = JsonDocument.Parse(json);
            await OutboxTests.ExecuteAsync(before.ConnectionString, $"""
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{page}','{Space}','{Writer}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
                  ('{working}','{page}',1,'Živá','{json}','živá','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
                INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
                  ('{page}','{generation}','{working}',0,0,0, now());
                """);
            // The live document as a 3.5 editor left it: seeded from the revision in the block shape, then a paragraph typed and never saved.
            var document = new WikiDocumentId(Guid.Parse(page), Guid.Parse(generation)).ToString();
            Assert.True(await writer.SeedAsync(document, host, token => authority.SeedAsync(host, stored.RootElement.GetProperty("document"), token), default));
            await using var hold = (await writer.HoldAsync(document, default))!;
            var typed = JsonNode.Parse(new YjsDocumentMaterializer(HostDocumentValidator.Instance.Schema).Materialize(hold.Snapshot, hold.Updates.Select(x => (ReadOnlyMemory<byte>)x)))!;
            typed["content"]!.AsArray().Add(JsonNode.Parse(Paragraph("Napsáno živě")));
            using var target = JsonDocument.Parse(typed.ToJsonString());
            var update = await authority.WriteAsync(host, hold.Snapshot, hold.Updates, target.RootElement, hold.Sequence + 1, default);
            await hold.AppendAsync(new CollabUser(Writer, "Writer"), update, new UpdateOrigin(RequestChannel.Web.Code), default);
        }
        await before.StopAsync();
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """UPDATE "PageCollaboration" SET "UpdatedAt" = now() - interval '1 hour'""");
        Assert.Equal("1:0,1:0", await OutboxTests.ScalarAsync(schema.ConnectionString, """SELECT string_agg("Sequence" || ':' || "LastSavedSequence", ',') FROM "PageCollaboration" """));

        // The upgraded server: the job acts as whoever typed (the real actor of a job), no browser is connected.
        await using var after = await AtlasTestHost.CreateAsync(schema, services =>
        {
            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentActor, HttpCurrentActor>();
        }, modules: TestModules.AllEnabled);
        var run = await after.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");

        // Each checkpoint's save reads its page back, and that read converts it (repair on open, the same conversion with its gate, budget
        // and backup): the batch finds nothing left, nothing is deferred.
        Assert.Equal("succeeded,0,0,0,0,0", await OutboxTests.ScalarAsync(after.ConnectionString, $"""
            SELECT concat_ws(',', "State", "Progress"->>'pages', "Progress"->>'revisions', "Progress"->>'deferred', "Progress"->>'refused', "Progress"->>'failed') FROM "JobRun" WHERE "Id" = {run}
            """));
        foreach (var (page, working, generation, block) in pages)
        {
            // The typed paragraph is the writer's revision (web), converted in place with its original in the backup; the revision before
            // it stays in history as stored.
            Assert.Equal(1L, await OutboxTests.ScalarAsync(after.ConnectionString, $"""
                SELECT count(*) FROM "RequirementConversionBackup" b JOIN "PageRevision" r ON r."Id" = b."RevisionId"
                WHERE r."PageId"='{page}' AND r."ActiveTo" IS NULL AND strpos(b."ContentJson", 'Napsáno živě') > 0 AND strpos(b."ContentJson", '"type":"requirement"') > 0
                """));
            Assert.Equal($"2:web:{Writer}:true:false:true", await OutboxTests.ScalarAsync(after.ConnectionString, $"""
                SELECT r."Version" || ':' || r."Channel" || ':' || r."AuthorId" || ':' || (strpos(r."ContentJson", 'Napsáno živě') > 0) || ':'
                  || (strpos(r."ContentJson", '"type":"requirement"') > 0) || ':' || (strpos(r."ContentJson", '"headingId":"zive"') > 0)
                FROM "PageRevision" r WHERE r."PageId"='{page}' AND r."ActiveTo" IS NULL
                """));
            Assert.Equal(Envelope(Block(block, "zive", "Rozepsaný požadavek"), Paragraph("Text")), await OutboxTests.ScalarAsync(after.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{working}'"""));
            // Nothing unsaved is left, and the live document starts again from the converted revision.
            Assert.Equal("0:0:false", await OutboxTests.ScalarAsync(after.ConnectionString, $"""
                SELECT "Sequence" || ':' || "LastSavedSequence" || ':' || ("Generation" = '{generation}') FROM "PageCollaboration" WHERE "PageId"='{page}'
                """));
        }
    }

    private static async Task WaitAsync(AtlasTestHost host, string sql, long expected)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((long)(await OutboxTests.ScalarAsync(host.ConnectionString, sql))! != expected)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(40), $"Waiting for {expected} of: {sql}");
            await Task.Delay(100);
        }
    }

    /// <summary>A page an earlier 3.6 build converted (atlas-requirements 3) that still holds a disclosure, as on an instance that ran it:
    /// the job moves its party into the heading and converts the content as it is read (the disclosure a heading), never refused by the
    /// schema check for the disclosure the read repair removes.</summary>
    [PostgreSqlFact]
    public async Task A_version_3_page_with_a_disclosure_moves_its_party_in_the_job()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_move_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110700);
        const string Facts = """{"type":"paragraph","content":[{"type":"text","text":"od Správa sítě · do 20. 11. 2026"}]}""";
        const string Disclosure = """{"type":"disclosure","attrs":{"title":"Podrobnosti"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Text"}]}]}""";
        var json = Envelope(Heading(2, "h-sit", "Síť"), """{"type":"heading","attrs":{"level":3,"headingId":"portal.dns","state":"planned","direction":"inbound"},"content":[{"type":"text","text":"DNS záznam"}]}""",
            Facts, Disclosure).Replace("\"version\":2}", "\"version\":3}", StringComparison.Ordinal);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Live}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{LiveWorking}','{Live}',1,'Zakázka','{json}','a','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);

        await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");

        var stored = (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{LiveWorking}'"""))!;
        Assert.Contains("\"party\":\"Správa sítě\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"do 20. 11. 2026\"", stored, StringComparison.Ordinal);
        Assert.Contains("{\"id\":\"atlas-requirements\",\"version\":4}", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"disclosure\"", stored, StringComparison.Ordinal);
    }
}
