using System.Text.Json.Nodes;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Pages.RequirementHeadingConverterTests;

namespace Atlas.Server.Tests.Pages;

/// <summary>Migration 2026110700 and the job pages.requirements-headings over invented pages shaped like real ones: a deployment page
/// whose working revision added a block (and an older history revision), a page depending on it, a template, a page with unsaved live edits that also depends on a page an administrator rolled back, the
/// rolled-back page, a page without requirements and a page whose note would read as a facts line. Migration 2026110700 creates the backup
/// table and 2026110830 queues the job. The dry run writes nothing and counts what the live run then does; the live run converts in place with the
/// originals in the backup, the same heading id in the working and published revision, history, the live, the rolled-back and the
/// refused page as stored; the next runs convert the live page once saved (its dependencies resolved through the converted page and the
/// requirement index) and then change nothing.</summary>
public sealed class RequirementsHeadingsJobTests
{
    private const long Version = 2026110700;
    private const int LargeCount = 150;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
        Deployment = "00000000-0000-0000-0000-0000000000a1", Dependent = "00000000-0000-0000-0000-0000000000a2", Template = "00000000-0000-0000-0000-0000000000a3",
        Live = "00000000-0000-0000-0000-0000000000a4", Rolled = "00000000-0000-0000-0000-0000000000a5", Plain = "00000000-0000-0000-0000-0000000000a6",
        Refused = "00000000-0000-0000-0000-0000000000a7",
        History = "00000000-0000-0000-0000-0000000000b0", Published = "00000000-0000-0000-0000-0000000000b1", Working = "00000000-0000-0000-0000-0000000000b2",
        DependentWorking = "00000000-0000-0000-0000-0000000000b3", TemplateWorking = "00000000-0000-0000-0000-0000000000b4", LiveWorking = "00000000-0000-0000-0000-0000000000b5",
        RolledWorking = "00000000-0000-0000-0000-0000000000b6", PlainWorking = "00000000-0000-0000-0000-0000000000b7", RefusedWorking = "00000000-0000-0000-0000-0000000000b8",
        Vpn = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601", Backup = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e602", Dns = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e603";

    private static readonly string PublishedJson = Envelope(Heading(2, "h-sit", "Síť"), Block(Vpn, "portal.vpn", "VPN tunel", state: "done"),
        Block(Dns, "portal.dns", "DNS záznam", requires: "portal.vpn", direction: "inbound", party: "Správa sítě", date: "2026-11-20"));
    private static readonly string WorkingJson = Envelope(Heading(2, "h-sit", "Síť"), Block(Vpn, "portal.vpn", "VPN tunel", state: "done"),
        Block(Backup, "portal.zaloha", "Záložní VPN"), Block(Dns, "portal.dns", "DNS záznam", requires: "portal.vpn", direction: "inbound", party: "Správa sítě",
            date: "2026-11-20", body: [Paragraph("Čekáme na potvrzení")]));
    private static readonly string DependentJson = Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e611", "aplikace", "Aplikace",
        requires: $"portal.dns@{Deployment}"));
    private static readonly string TemplateJson = Envelope(Heading(3, "h-jadro", "Jádro"), Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e621", "jadro.db", "Databáze"));
    private static readonly string LiveJson = Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e631", "zive", "Rozepsaný požadavek",
        requires: $"portal.dns@{Deployment},vraceny.klic@{Rolled}"));
    private static readonly string RolledJson = Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e641", "vraceny.klic", "Vrácený požadavek"));
    private static readonly string RefusedJson = Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e651", "smlouva", "Smlouva", body: [Paragraph("do 1. 12. 2026")]));

    [PostgreSqlFact]
    public async Task Migration_2026110700_queues_nothing_and_the_job_dry_runs_then_converts_with_backups_and_changes_nothing_after()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_headings_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Deployment}','{Space}','{Writer}',3), ('{Dependent}','{Space}','{Writer}',1),
              ('{Live}','{Space}','{Writer}',1), ('{Rolled}','{Space}','{Writer}',1), ('{Plain}','{Space}','{Writer}',1), ('{Refused}','{Space}','{Writer}',1);
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","IsTemplate") VALUES ('{Template}','{Space}','{Writer}',1,true);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{History}','{Deployment}',1,'Nasazení','{PublishedJson}','starší','{Writer}','2026-10-01T10:00:00Z','2026-10-02T10:00:00Z','web'),
              ('{Published}','{Deployment}',2,'Nasazení','{PublishedJson}','publikovaná','{Writer}','2026-10-02T10:00:00Z','2026-10-03T10:00:00Z','web'),
              ('{Working}','{Deployment}',3,'Nasazení','{WorkingJson}','pracovní','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{DependentWorking}','{Dependent}',1,'Aplikace','{DependentJson}','aplikace','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{TemplateWorking}','{Template}',1,'Šablona','{TemplateJson}','šablona','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{LiveWorking}','{Live}',1,'Živá','{LiveJson}','živá','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{RolledWorking}','{Rolled}',1,'Vrácená','{RolledJson}','vrácená','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{PlainWorking}','{Plain}',1,'Obyčejná','{Envelope(Paragraph("text"))}','text','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{RefusedWorking}','{Refused}',1,'Smlouva','{RefusedJson}','smlouva','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='{Published}' WHERE "Id"='{Deployment}';
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Deployment}','00000000-0000-0000-0000-0000000000c1','{Working}',2,2,2,'2026-10-03T10:00:00Z'),
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',3,1,1,'2026-10-03T10:00:00Z');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        // Migration 2026110830 queued the job once, for the pages that hold requirement blocks. The rolled-back page as a restore leaves it.
        Assert.Equal("1,Aktualizace na verzi 3.6", await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT count(*) || ',' || min("Reason") FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}'"""));
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "RequirementConversionBackup"("RevisionId","PageId","ContentJson","Markdown","RolledBack") VALUES('{RolledWorking}','{Rolled}','{RolledJson}','vrácená',true);
            """);
        var original = await AllContentAsync(host);
        var runner = host.Services.GetRequiredService<IJobRunner>();

        // The dry run reads five pages (not the rolled-back one, not the plain one) and writes nothing but its own run.
        var dry = await RunAsync(host, await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test", RequirementsHeadingsJob.DryRunKey));
        Assert.Equal(original, await AllContentAsync(host));
        Assert.Equal(1L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "RequirementConversionBackup" """));
        Assert.Equal("2,2", await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "Sequence" || ',' || "LastSavedSequence" FROM "PageCollaboration" WHERE "PageId"='{Deployment}'"""));

        var live = await RunAsync(host, await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test"));

        // The same pages, requirements, cases, losses and gains; the dry run counts the live page's revision the live run defers.
        Assert.Equal("succeeded,5,5,0,1,9,0,1,1", dry);
        Assert.Equal("succeeded,5,4,1,1,9,0,1,1", live);
        // The originals of the converted revisions are in the backup; history, the live, the rolled-back and the refused page as stored.
        Assert.Equal(4L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "RequirementConversionBackup" b WHERE NOT b."RolledBack" AND b."ContentJson" = CASE b."RevisionId"
              WHEN '{Published}' THEN '{PublishedJson}' WHEN '{Working}' THEN '{WorkingJson}' WHEN '{DependentWorking}' THEN '{DependentJson}' WHEN '{TemplateWorking}' THEN '{TemplateJson}' END
              AND b."Markdown" = CASE b."RevisionId" WHEN '{Published}' THEN 'publikovaná' WHEN '{Working}' THEN 'pracovní' WHEN '{DependentWorking}' THEN 'aplikace' WHEN '{TemplateWorking}' THEN 'šablona' END
            """));
        foreach (var (revision, json) in new[] { (History, PublishedJson), (LiveWorking, LiveJson), (RolledWorking, RolledJson), (RefusedWorking, RefusedJson) })
        {
            Assert.Equal(json, await ContentAsync(host, revision));
        }
        // A block has one heading id in both revisions: its key (the dot allowed).
        Assert.Equal("portal.vpn,portal.dns", HeadingIds(await ContentAsync(host, Published)));
        Assert.Equal("portal.vpn,portal.zaloha,portal.dns", HeadingIds(await ContentAsync(host, Working)));
        Assert.Equal("jadro.db", HeadingIds(await ContentAsync(host, TemplateWorking)));
        var dependent = Document(await ContentAsync(host, DependentWorking));
        Assert.Equal($"#/pages/{Deployment}?heading=portal.dns", (string?)dependent["content"]![1]!["content"]![0]!["attrs"]!["url"]);
        Assert.Contains("DNS záznam", (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "Markdown" FROM "PageRevision" WHERE "Id"='{Working}'"""))!, StringComparison.Ordinal);
        // In place: the author, time and version stay; the converted page's live state starts again, the live page keeps its own.
        Assert.Equal("1,2,3", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg("Version"::text, ',' ORDER BY "Version") FROM "PageRevision"
            WHERE "PageId"='{Deployment}' AND "AuthorId"='{Writer}' AND "ActiveFrom" = timestamptz '2026-10-01T10:00:00Z' + ("Version" - 1) * interval '1 day'
            """));
        Assert.Equal("0,false", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT "Sequence" || ',' || ("Generation" = '00000000-0000-0000-0000-0000000000c1') FROM "PageCollaboration" WHERE "PageId"='{Deployment}'
            """));
        Assert.Equal("3,true", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT "Sequence" || ',' || ("Generation" = '00000000-0000-0000-0000-0000000000c2') FROM "PageCollaboration" WHERE "PageId"='{Live}'
            """));
        // Stale unsaved edits (nobody active for days): the page waits as stored, nothing of them is lost, and no follow-up run is requested every ten minutes for it.
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}' AND "State"='queued' AND "NotBefore" > now()
            """));

        // Saved and idle, the live page converts: its dependencies resolve through the converted page and the rolled-back page's index rows.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            UPDATE "PageCollaboration" SET "LastSavedSequence"="Sequence" WHERE "PageId"='{Live}';
            DELETE FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}' AND "State"='queued';
            """);
        Assert.Equal("succeeded,2,1,0,1,2,0,1,1", await RunAsync(host, await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test")));
        var converted = Document(await ContentAsync(host, LiveWorking))["content"]![1]!;
        Assert.Equal(new[] { $"#/pages/{Deployment}?heading=portal.dns", $"#/pages/{Rolled}?heading=vraceny.klic" },
            converted["content"]!.AsArray().Where(x => (string?)x!["type"] == "pageReference").Select(x => (string?)x!["attrs"]!["url"]));
        Assert.Equal(RolledJson, await ContentAsync(host, RolledWorking));

        // Nothing left but the refused page: a further run changes nothing and asks for nothing.
        var after = await AllContentAsync(host);
        Assert.Equal("succeeded,1,0,0,1,1,0,1,1", await RunAsync(host, await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test")));
        Assert.Equal(after, await AllContentAsync(host));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}' AND "State"='queued'"""));
    }

    [Fact]
    public void The_approved_budget_admits_cases_up_to_its_counts_and_refuses_one_beyond_or_of_another_kind()
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);

        Assert.Null(RequirementPageConversion.Admit(new Dictionary<RequirementCaseKind, int> { [RequirementCaseKind.LooseContent] = 4, [RequirementCaseKind.Nested] = 1 }, used));
        Assert.Null(RequirementPageConversion.Admit(new Dictionary<RequirementCaseKind, int> { [RequirementCaseKind.LooseContent] = 3 }, used));
        Assert.NotNull(RequirementPageConversion.Admit(new Dictionary<RequirementCaseKind, int> { [RequirementCaseKind.LooseContent] = 1 }, used));
        Assert.NotNull(RequirementPageConversion.Admit(new Dictionary<RequirementCaseKind, int> { [RequirementCaseKind.Nested] = 1 }, used));
        // A kind the owner did not approve refuses even once; a refused page uses nothing.
        Assert.NotNull(RequirementPageConversion.Admit(new Dictionary<RequirementCaseKind, int> { [RequirementCaseKind.UnresolvedKey] = 1, [RequirementCaseKind.EmptyTitle] = 1 }, used));
        Assert.Equal(new Dictionary<string, int> { ["LooseContent"] = 7, ["Nested"] = 1 }, used);
        // The working and the published revision report one block's case once.
        var report = RequirementConversionReport.Compare(Guid.Empty, [], [], [new(RequirementCaseKind.EmptyTitle, "b", ""), new(RequirementCaseKind.IdFallback, "b", "no key")]);
        Assert.Equal(new Dictionary<RequirementCaseKind, int> { [RequirementCaseKind.EmptyTitle] = 1, [RequirementCaseKind.IdFallback] = 1 },
            RequirementPageConversion.PageCases([report, report]));
    }

    /// <summary>Eight pages with one loose block each (a reported case the owner approved seven of), around 46 pages without a case so the
    /// run takes two batches, and a page with an unresolved key (a kind not approved). The budget is used across the batches: seven pages
    /// convert, the eighth and the unresolved one keep their shape; a re-run reads only those two, uses nothing again and changes nothing.</summary>
    [PostgreSqlFact]
    public async Task The_approved_cases_are_a_budget_over_batches_and_runs()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_budget_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        var loose = Envelope(Block(Vpn, "pozadavek", "Požadavek"), Paragraph("Volný text pod požadavkem"));
        var plain = Envelope(Heading(2, "h", "Moduly"), Block(Vpn, "modul", "Modul"));
        var unresolved = Envelope(Block(Vpn, "aplikace", "Aplikace", requires: "neni"));
        string Id(int number) => $"00000000-0000-0000-0000-{number:x12}";
        var loosePages = new[] { 0x101, 0x102, 0x103, 0x301, 0x302, 0x303, 0x304, 0x305 };
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT p::uuid, '{Space}'::uuid, '{Writer}'::uuid, 1 FROM unnest(ARRAY[{string.Join(",", loosePages.Select(x => $"'{Id(x)}'"))}, '{Id(0x3ff)}']) p
              UNION ALL SELECT ('00000000-0000-0000-0000-' || lpad(to_hex(512 + n), 12, '0'))::uuid, '{Space}'::uuid, '{Writer}'::uuid, 1 FROM generate_series(1, 46) n;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
              SELECT gen_random_uuid(), p."Id", 1, 'Stránka',
                CASE WHEN p."Id" = '{Id(0x3ff)}' THEN '{unresolved}' WHEN p."Id" = ANY(ARRAY[{string.Join(",", loosePages.Select(x => $"'{Id(x)}'::uuid"))}]) THEN '{loose}' ELSE '{plain}' END,
                'text', '{Writer}'::uuid, timestamptz '2026-10-03T10:00:00Z', NULL::timestamptz, 'web' FROM "Page" p;
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        var runner = host.Services.GetRequiredService<IJobRunner>();

        var first = await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");

        // 55 pages in two batches: 53 convert (46 without a case, 7 loose), the eighth loose page and the unresolved one are refused.
        Assert.Equal("succeeded,55,53,0,2,55,9,0,0", await RunAsync(host, first));
        Assert.Equal("{\"LooseContent\": 7}", await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT CAST("Progress"->'used' AS text) FROM "JobRun" WHERE "Id"={first}"""));
        Assert.Equal(loose, await ContentOfPageAsync(host, Id(0x305)));
        Assert.Equal(unresolved, await ContentOfPageAsync(host, Id(0x3ff)));
        Assert.DoesNotContain("\"type\":\"requirement\"", await ContentOfPageAsync(host, Id(0x304)), StringComparison.Ordinal);

        // A re-run reads only the two refused pages; the converted ones use nothing again, so the budget stays spent and nothing changes.
        var converted = await AllContentAsync(host);
        var second = await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        Assert.Equal("succeeded,2,0,0,2,2,2,0,0", await RunAsync(host, second));
        Assert.Equal("{\"LooseContent\": 7}", await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT CAST("Progress"->'used' AS text) FROM "JobRun" WHERE "Id"={second}"""));
        Assert.Equal(converted, await AllContentAsync(host));
    }

    /// <summary>A large page as production has them, larger: 150 requirements, about 2 000 paragraphs (13 notes each). The job converts one
    /// and an open converts another, each well within a generous bound (60 s, against a command timeout of 30 s per statement before: the
    /// index triggers recomputed the page at the commit), and the index refresh of a converted page stays within 5 s; the measured times are
    /// written out. One page whose revision write fails (a test trigger raising, as a timeout would) is counted as failed and skipped, the run
    /// goes on and asks for no other run (a page that always fails must not come back every ten minutes); the next run, the failure gone,
    /// continues and converts it.</summary>
    [PostgreSqlFact]
    public async Task A_large_page_converts_quickly_and_a_failing_page_is_skipped_and_retried()
    {
        string Large(string prefix) => Envelope([Heading(2, prefix + "-moduly", "Moduly"), .. Enumerable.Range(1, LargeCount).Select(i => Block(
            $"6f1d2c3b-4a5e-4f60-8a71-{i:x12}", $"{prefix}.pozadavek-{i}", $"Požadavek {i}", direction: i % 3 == 0 ? "inbound" : "internal",
            party: i % 5 == 0 ? "Dodavatel" : "", date: i % 7 == 0 ? "2026-12-01" : "", requires: i > 1 ? $"{prefix}.pozadavek-{i - 1}" : "",
            body: [.. Enumerable.Range(1, 13).Select(n => Paragraph($"Poznámka {n} k požadavku {i} s nějakým delším textem, aby stránka měla skutečnou velikost."))]))]);
        const string ByJob = "00000000-0000-0000-0000-0000000000a1", ByOpen = "00000000-0000-0000-0000-0000000000a2", Failing = "00000000-0000-0000-0000-0000000000a3",
            ByJobWorking = "00000000-0000-0000-0000-0000000000b1", ByOpenWorking = "00000000-0000-0000-0000-0000000000b2", FailingWorking = "00000000-0000-0000-0000-0000000000b3";
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_large_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{ByJob}','{Space}','{Writer}',1), ('{ByOpen}','{Space}','{Writer}',1), ('{Failing}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{ByJobWorking}','{ByJob}',1,'Velká','{Large("job")}','velká','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{ByOpenWorking}','{ByOpen}',1,'Velká','{Large("open")}','velká','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{FailingWorking}','{Failing}',1,'Selhávající','{Envelope(Block(Vpn, "selhani", "Selhání"))}','selhání','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id";
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        // The open is timed on its own: the job does not reach that page in this run.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "RequirementConversionBackup"("RevisionId","PageId","ContentJson","Markdown","RolledBack") VALUES('00000000-0000-0000-0000-0000000000ff','{ByOpen}','','',true);
            CREATE FUNCTION w1_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$;
            CREATE TRIGGER "TR_W1_Fail" BEFORE UPDATE OF "ContentJson" ON "PageRevision" FOR EACH ROW WHEN (OLD."Id" = '{FailingWorking}') EXECUTE FUNCTION w1_fail();
            """);
        var runner = host.Services.GetRequiredService<IJobRunner>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        var job = clock.Elapsed;

        // Two pages read: the large one converted, the failing one skipped and counted; no other run is asked for.
        Assert.Equal("succeeded,2,1,0,0,1", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT concat_ws(',', "State", "Progress"->>'pages', "Progress"->>'revisions', "Progress"->>'deferred', "Progress"->>'refused', "Progress"->>'failed')
            FROM "JobRun" WHERE "Id"={first}
            """));
        Assert.Equal(150, HeadingIds(await ContentAsync(host, ByJobWorking)).Split(',').Length);
        Assert.Contains("\"type\":\"requirement\"", await ContentAsync(host, FailingWorking), StringComparison.Ordinal);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}' AND "State"='queued'
            """));

        // The open converts the other large page once its rollback mark is gone.
        await OutboxTests.ExecuteAsync(host.ConnectionString, """DELETE FROM "RequirementConversionBackup" WHERE "RolledBack" """);
        host.Actor.Current = new UserAccount { Id = Guid.Parse(Writer), Username = "writer", DisplayName = "Writer", IsWikiAdmin = true, IsActive = true, SecurityStamp = "stamp" };
        clock.Restart();
        var opened = await host.InvokeAsync<Features.Pages.Contracts.PageDto>(new Features.Pages.Queries.GetPage(Guid.Parse(ByOpen)));
        var open = clock.Elapsed;
        Assert.Equal(150, HeadingIds(opened.ContentJson).Split(',').Length);

        // The index of a converted large page, as the outbox's delivery refreshes it.
        clock.Restart();
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await ProjectorIndex.RefreshAsync(session, [Guid.Parse(ByJob)], default);
            await transaction.CommitAsync();
        }
        var refresh = clock.Elapsed;
        Assert.Equal(150L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT count(*) FROM "ProjectorRequirementIndex" WHERE "PageId"='{ByJob}'"""));
        Console.WriteLine($"large page: job {job.TotalMilliseconds:0} ms (two pages), open {open.TotalMilliseconds:0} ms, index refresh {refresh.TotalMilliseconds:0} ms");
        Assert.True(job < TimeSpan.FromSeconds(60) && open < TimeSpan.FromSeconds(60) && refresh < TimeSpan.FromSeconds(5),
            $"job {job}, open {open}, refresh {refresh}");

        // The failure gone, the next run continues: only the failing page is still in the old shape, and it converts.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            DROP TRIGGER "TR_W1_Fail" ON "PageRevision";
            DELETE FROM "JobRun" WHERE "JobId"='{RequirementsHeadingsJob.JobId}' AND "State"='queued';
            """);
        var second = await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        Assert.Equal("succeeded,1,1,0", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT concat_ws(',', "State", "Progress"->>'pages', "Progress"->>'revisions', "Progress"->>'failed') FROM "JobRun" WHERE "Id"={second}
            """));
        Assert.Equal("selhani", HeadingIds(await ContentAsync(host, FailingWorking)));
    }

    /// <summary>Review 2026-11-05 (High): a block whose key the space's catalog defines keeps that definition as `from` through the job (the
    /// map reads the catalog pages of the converted page's space), whichever of the two pages converts first.</summary>
    [PostgreSqlFact]
    public async Task A_block_of_a_catalog_key_keeps_its_catalog_definition_as_from()
    {
        const string Catalog = "00000000-0000-0000-0000-0000000000a9", Deploy = "00000000-0000-0000-0000-0000000000a1",
            CatalogWorking = "00000000-0000-0000-0000-0000000000b9", DeployWorking = "00000000-0000-0000-0000-0000000000b1";
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_from_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Catalog}','{Space}','{Writer}',1), ('{Deploy}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{CatalogWorking}','{Catalog}',1,'Katalog','{Envelope(Block(Vpn, "portal.vpn", "VPN tunel"))}','katalog','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{DeployWorking}','{Deploy}',1,'Nasazení','{Envelope(Block(Dns, "portal.vpn", "VPN tunel"), Block(Backup, "vlastni", "Vlastní"))}','nasazení','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id";
            INSERT INTO "ProjectorCatalogPage"("PageId","MarkedById","MarkedAt") VALUES('{Catalog}','{Writer}',now());
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);

        Assert.Equal("succeeded,2,2,0,0,3,0,0,0", await RunAsync(host, await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementsHeadingsJob.JobId, "Test")));

        var headings = Document(await ContentAsync(host, DeployWorking))["content"]!.AsArray().Where(x => x!["attrs"]?["state"] is not null).ToList();
        Assert.Equal(new[] { $"{Catalog}#portal.vpn", "" }, headings.Select(x => (string?)x!["attrs"]!["from"]));
        Assert.Equal("", (string?)Document(await ContentAsync(host, CatalogWorking))["content"]![0]!["attrs"]!["from"]);
    }

    private static async Task<string> ContentOfPageAsync(AtlasTestHost host, string page)
        => (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "PageId"='{page}' AND "ActiveTo" IS NULL"""))!;

    private static JsonNode Document(string json) => JsonNode.Parse(json)!["document"]!;

    private static string HeadingIds(string json) => string.Join(',', Document(json)["content"]!.AsArray()
        .Where(x => (string?)x!["type"] == "heading" && x["attrs"]!["state"] is not null).Select(x => (string?)x!["attrs"]!["headingId"]));

    private static async Task<string> RunAsync(AtlasTestHost host, long runId) => (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""
        SELECT concat_ws(',', "State", "Progress"->>'pages', "Progress"->>'revisions', "Progress"->>'deferred', "Progress"->>'refused',
          "Progress"->>'requirements', "Progress"->>'cases', "Progress"->>'losses', "Progress"->>'gains') FROM "JobRun" WHERE "Id"={runId}
        """))!;

    private static async Task<string> ContentAsync(AtlasTestHost host, string revision)
        => (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{revision}'"""))!;

    private static async Task<string> AllContentAsync(AtlasTestHost host) => (string)(await OutboxTests.ScalarAsync(host.ConnectionString,
        """SELECT string_agg("Id"::text || "ContentJson" || "Markdown", E'\n' ORDER BY "Id") FROM "PageRevision" """))!;
}
