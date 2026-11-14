using System.Text.Json.Nodes;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Validation;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>Migration 2026110480 and the job pages.requirements-overview over pages as 3.3 stored them: a deployment page whose working,
/// published and older history revisions hold the deployment summary <c>requirementSummary</c> under <c>atlas-requirements</c> 1, a
/// template, a page someone is editing live, a page open in an editor without unsaved edits, a page whose stored JSON cannot be read
/// and a page without requirements. The migration queues one run; the run converts the working and published revisions and the template
/// in place (author and time kept), leaves history, the live and the open page alone, counts the unreadable one as refused and asks for
/// another run, which converts the live and the open page once their edits are saved and the editor idle; a run over converted content,
/// edited live again, reads nothing and asks for nothing.</summary>
public sealed class RequirementsOverviewJobTests
{
    private const long Version = 2026110480;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
        Deployment = "00000000-0000-0000-0000-0000000000a1", Template = "00000000-0000-0000-0000-0000000000a2", Live = "00000000-0000-0000-0000-0000000000a3",
        Plain = "00000000-0000-0000-0000-0000000000a4", History = "00000000-0000-0000-0000-0000000000b1", Published = "00000000-0000-0000-0000-0000000000b2",
        Working = "00000000-0000-0000-0000-0000000000b3", TemplateWorking = "00000000-0000-0000-0000-0000000000b4", LiveWorking = "00000000-0000-0000-0000-0000000000b5",
        PlainWorking = "00000000-0000-0000-0000-0000000000b6", Open = "00000000-0000-0000-0000-0000000000a5", OpenWorking = "00000000-0000-0000-0000-0000000000b7",
        Broken = "00000000-0000-0000-0000-0000000000a0", BrokenWorking = "00000000-0000-0000-0000-0000000000b0", Summary = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f0";

    /// <summary>A 3.3 page: the summary first, then one requirement block.</summary>
    private static string Shape33(string text) => $$$"""
        {"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":1}],"document":{"type":"doc","content":[{"type":"requirementSummary","attrs":{"id":"{{{Summary}}}"}},{"type":"requirement","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f7","key":"vpn","direction":"internal","state":"planned","date":"","party":"","where":"","requires":""},"content":[{"type":"paragraph","content":[{"type":"text","text":"{{{text}}}"}]}]}]}}
        """;

    /// <summary>A 3.3 page whose envelope repeats a property: reading it throws ArgumentException, not a validation error.</summary>
    private static string Unreadable(string text) => "{\"format\":\"atlas-rich\"," + Shape33(text)[1..];

    [PostgreSqlFact]
    public async Task Migration_2026110480_queues_the_conversion_which_converts_current_content_in_place_and_leaves_history_and_live_pages()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_overview_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Deployment}','{Space}','{Writer}',3), ('{Live}','{Space}','{Writer}',1), ('{Plain}','{Space}','{Writer}',1),
              ('{Open}','{Space}','{Writer}',1), ('{Broken}','{Space}','{Writer}',1);
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","IsTemplate") VALUES ('{Template}','{Space}','{Writer}',1,true);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{History}','{Deployment}',1,'Zakázka','{Shape33("první")}','první','{Writer}','2026-10-01T10:00:00Z','2026-10-02T10:00:00Z','web'),
              ('{Published}','{Deployment}',2,'Zakázka','{Shape33("druhá")}','druhá','{Writer}','2026-10-02T10:00:00Z','2026-10-03T10:00:00Z','web'),
              ('{Working}','{Deployment}',3,'Zakázka','{Shape33("třetí")}','třetí','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{TemplateWorking}','{Template}',1,'Předvolba','{Shape33("vzor")}','vzor','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{LiveWorking}','{Live}',1,'Rozepsaná','{Shape33("živá")}','živá','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{PlainWorking}','{Plain}',1,'Obyčejná','{AttachmentFixture.Rich("text")}','text','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{OpenWorking}','{Open}',1,'Otevřená','{Shape33("otevřená")}','otevřená','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{BrokenWorking}','{Broken}',1,'Rozbitá','{Unreadable("rozbitá")}','rozbitá','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='{Published}' WHERE "Id"='{Deployment}';
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Deployment}','00000000-0000-0000-0000-0000000000c1','{Working}',2,2,2,'2026-10-03T10:00:00Z'),
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',3,1,1,'2026-10-03T10:00:00Z'),
              ('{Open}','00000000-0000-0000-0000-0000000000c3','{OpenWorking}',0,0,0,now());
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal("1,Aktualizace na verzi 3.4", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) || ',' || min("Reason") FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued'
            """));
        var runner = host.Services.GetRequiredService<IJobRunner>();

        var first = await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test");

        // Five pages hold the 3.3 shape; the working and published revisions and the template are converted, the live page and the
        // page open in an editor wait, the unreadable one is refused without stopping the others.
        Assert.Equal("succeeded,5,3,2,1", await RunAsync(host, first));
        foreach (var revision in new[] { Published, Working, TemplateWorking })
        {
            var root = JsonNode.Parse((string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{revision}'"""))!)!;
            // The repair stamps the CURRENT atlas-requirements version (3 since the requirement-heading schema).
            Assert.Equal(CollaborationSchema.CurrentPluginVersions["atlas-requirements"], (int)root["plugins"]![0]!["version"]!);
            Assert.Equal("overview", (string?)root["document"]!["content"]![0]!["type"]);
            Assert.Equal(Summary, (string?)root["document"]!["content"]![0]!["attrs"]!["id"]);
            Assert.Equal("requirements", (string?)root["document"]!["content"]![0]!["attrs"]!["kind"]);
        }
        // In place: the author, time, channel and version of each revision stay, so nothing looks edited; history and the live page as stored.
        Assert.Equal("1,2,3", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg("Version"::text, ',' ORDER BY "Version") FROM "PageRevision"
            WHERE "PageId"='{Deployment}' AND "AuthorId"='{Writer}' AND "ActiveFrom" = timestamptz '2026-10-01T10:00:00Z' + ("Version" - 1) * interval '1 day'
            """));
        Assert.Equal("3", (await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "Version" FROM "Page" WHERE "Id"='{Deployment}'"""))!.ToString());
        Assert.Equal(Shape33("první"), await ContentAsync(host, History));
        Assert.Equal(Shape33("živá"), await ContentAsync(host, LiveWorking));
        Assert.Equal(Shape33("otevřená"), await ContentAsync(host, OpenWorking));
        Assert.Equal(Unreadable("rozbitá"), await ContentAsync(host, BrokenWorking));
        // The converted page's live state starts again from the converted revision; the live one keeps its generation and unsaved updates.
        Assert.Equal("0,false", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT "Sequence" || ',' || ("Generation" = '00000000-0000-0000-0000-0000000000c1') FROM "PageCollaboration" WHERE "PageId"='{Deployment}'
            """));
        Assert.Equal("3,true", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT "Sequence" || ',' || ("Generation" = '00000000-0000-0000-0000-0000000000c2') FROM "PageCollaboration" WHERE "PageId"='{Live}'
            """));
        // The deferred page asks for another run later.
        Assert.Equal(1L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued' AND "NotBefore" > now()
            """));

        // A second run reads only what is still in the 3.3 shape and changes nothing.
        var converted = await AllContentAsync(host);
        Assert.Equal("succeeded,3,0,2,1", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        Assert.Equal(converted, await AllContentAsync(host));

        // Once the live edits are saved and the open editor idle, the next run converts those pages too.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            UPDATE "PageCollaboration" SET "LastSavedSequence"="Sequence" WHERE "PageId"='{Live}';
            UPDATE "PageCollaboration" SET "UpdatedAt"=now() - interval '1 hour' WHERE "PageId"='{Open}';
            """);
        Assert.Equal("succeeded,3,2,0,1", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        Assert.DoesNotContain("requirementSummary", await ContentAsync(host, LiveWorking));
        Assert.DoesNotContain("requirementSummary", await ContentAsync(host, OpenWorking));
        Assert.Equal(Shape33("první"), await ContentAsync(host, History));

        // Converted pages edited live again (3.4 content) are neither read nor deferred: the run asks for no other run.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            UPDATE "PageCollaboration" SET "Sequence"=3 WHERE "PageId" IN ('{Deployment}','{Live}');
            DELETE FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued';
            """);
        Assert.Equal("succeeded,1,0,0,1", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued'
            """));
    }

    /// <summary>Unsaved live edits under a current snapshot: a page whose live text differs waits; a page whose live document only has the
    /// summary as the 3.4 editor shows it (the overview) reads as its working revision, so it is converted and its reset loses nothing: the
    /// converted revision is the live document.</summary>
    [PostgreSqlFact]
    public async Task A_current_snapshot_defers_changed_text_and_converts_a_live_overview_without_loss()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_overview_live_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        var overview = JsonNode.Parse(Shape33("stejná"))!["document"]!;
        overview["content"]![0]!["type"] = "overview";
        overview["content"]![0]!["attrs"]!["kind"] = "requirements";
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Live}','{Space}','{Writer}',1), ('{Open}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{LiveWorking}','{Live}',1,'Rozepsaná','{Shape33("živá")}','živá','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{OpenWorking}','{Open}',1,'Stejná','{Shape33("stejná")}','stejná','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt",
                "State","StateSha256","ProjectionJson","ProjectionMarkdown","ProjectionText") VALUES
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',2,1,2,'2026-10-03T10:00:00Z',decode('00','hex'),repeat('a',64),
                '{JsonNode.Parse(Shape33("změněná"))!["document"]!.ToJsonString()}','','x'),
              ('{Open}','00000000-0000-0000-0000-0000000000c3','{OpenWorking}',2,1,2,'2026-10-03T10:00:00Z',decode('00','hex'),repeat('a',64),
                '{overview.ToJsonString()}','','x');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);

        Assert.Equal("succeeded,2,1,1,0", await RunAsync(host, await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));

        Assert.Equal(Shape33("živá"), await ContentAsync(host, LiveWorking));
        Assert.True(JsonNode.DeepEquals(overview, JsonNode.Parse(await ContentAsync(host, OpenWorking))!["document"]));
    }

    /// <summary>A history line as MarkdownToRich read „- D. M. 2026 …“ before 3.5.1: the day's ordered list › the month's › the rest.</summary>
    private static string Dated(int day, int month, string inlines) => $$$"""
        {"type":"orderedList","attrs":{"tight":true,"start":{{{day}}}},"content":[{"type":"listItem","content":[{"type":"paragraph"},{"type":"orderedList","attrs":{"tight":true,"start":{{{month}}}},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{{{inlines}}}]}]}]}]}]}
        """;

    /// <summary>A 3.5.0 page: one requirement block holding <paramref name="notes"/>, then <paramref name="after"/> outside it.</summary>
    private static string Shape35(string notes, string after = "") => $$$"""
        {"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":2}],"document":{"type":"doc","content":[{"type":"requirement","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f7","key":"vpn","direction":"internal","state":"planned","date":"","party":"","where":"","requires":""},"content":[{"type":"paragraph","content":[{"type":"text","text":"VPN"}]},{{{notes}}}]}{{{after}}}]}}
        """;

    private static string BrokenHistory(string text) => Shape35($$"""
        {"type":"bulletList","attrs":{"tight":true},"content":[{"type":"listItem","content":[{"type":"paragraph"},{{Dated(30, 10, $$$"""{"type":"text","text":"2026 23:40 — "},{"type":"text","text":"{{{text}}}","marks":[{"type":"bold"}]}""")}}]}]}
        """);

    /// <summary>Migration 2026110550 and the same job over pages as 3.5.0 stored them: a deployment page whose working, published and
    /// older history revisions hold a history line read as nested ordered lists, and a page with another nested list in its requirement
    /// and the same shape outside it. The run folds the line in the working and published revisions in place (author and time kept, the
    /// Markdown written again), leaves history and the other page as stored, and a second run changes nothing.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110550_folds_history_lines_read_as_nested_lists_in_place_and_leaves_other_lists()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_history_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110550);
        var other = Shape35(Dated(2, 3, """{"type":"text","text":"krok"}"""), "," + Dated(30, 10, """{"type":"text","text":"2026 mimo požadavek"}"""));
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Deployment}','{Space}','{Writer}',3), ('{Plain}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{History}','{Deployment}',1,'Zakázka','{BrokenHistory("první")}','první','{Writer}','2026-10-01T10:00:00Z','2026-10-02T10:00:00Z','web'),
              ('{Published}','{Deployment}',2,'Zakázka','{BrokenHistory("druhá")}','druhá','{Writer}','2026-10-02T10:00:00Z','2026-10-03T10:00:00Z','web'),
              ('{Working}','{Deployment}',3,'Zakázka','{BrokenHistory("třetí")}','třetí','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{PlainWorking}','{Plain}',1,'Kroky','{other}','kroky','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='{Published}' WHERE "Id"='{Deployment}';
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal("1,Aktualizace na verzi 3.5.1 (data historie)", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) || ',' || min("Reason") FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued'
            """));
        var runner = host.Services.GetRequiredService<IJobRunner>();

        // Both pages hold an ordered list; the deployment page's working and published revisions are folded, and the plain page's working
        // revision is only re-stamped with the current plugin version (its 3.5.0 declaration is older), so three revisions are written.
        Assert.Equal("succeeded,2,3,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        foreach (var (revision, text) in new[] { (Published, "druhá"), (Working, "třetí") })
        {
            var item = JsonNode.Parse(await ContentAsync(host, revision))!["document"]!["content"]![0]!["content"]![1]!["content"]![0]!;
            Assert.Equal($$$"""
                {"type":"timedItem","content":[{"type":"paragraph","content":[{"type":"text","text":"30. 10. 2026 23:40 — "},{"type":"text","text":"{{{text}}}","marks":[{"type":"bold"}]}]}]}
                """, item.ToJsonString(RichDocument.JsonWrite));
            Assert.Contains($@"- 30\. 10. 2026 23:40 — **{text}**", (string)(await OutboxTests.ScalarAsync(host.ConnectionString,
                $"""SELECT "Markdown" FROM "PageRevision" WHERE "Id"='{revision}'"""))!, StringComparison.Ordinal);
        }
        Assert.Equal("1,2,3", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg("Version"::text, ',' ORDER BY "Version") FROM "PageRevision"
            WHERE "PageId"='{Deployment}' AND "AuthorId"='{Writer}' AND "ActiveFrom" = timestamptz '2026-10-01T10:00:00Z' + ("Version" - 1) * interval '1 day'
            """));
        Assert.Equal(BrokenHistory("první"), await ContentAsync(host, History));
        Assert.Equal(other.Replace("\"version\":2}", $"\"version\":{CollaborationSchema.CurrentPluginVersions["atlas-requirements"]}}}"), await ContentAsync(host, PlainWorking));

        // A second run reads only the page that still holds an ordered list and changes nothing.
        var repaired = await AllContentAsync(host);
        Assert.Equal("succeeded,1,0,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        Assert.Equal(repaired, await AllContentAsync(host));
    }

    /// <summary>A page as 3.5 stored a disclosure: its title in the attribute, on a page without requirements.</summary>
    private static string Shape35Disclosure(string text) => $$$"""
        {"format":"atlas-rich","version":1,"plugins":[],"document":{"type":"doc","content":[{"type":"disclosure","attrs":{"title":"Podrobnosti {{{text}}}"},"content":[{"type":"paragraph","content":[{"type":"text","text":"{{{text}}}"}]}]}]}}
        """;

    /// <summary>A page as 3.6 revision 6 stored a disclosure: its title a node, under a heading.</summary>
    private static string Shape36Disclosure(string text) => $$$"""
        {"format":"atlas-rich","version":1,"plugins":[],"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Sekce"}]},{"type":"disclosure","attrs":{"title":null},"content":[{"type":"disclosureTitle","content":[{"type":"text","text":"{{{text}}}","marks":[{"type":"bold"}]}]},{"type":"paragraph"}]}]}}
        """;

    /// <summary>Migration 2026110960 (kept unchanged: an instance that applied it must find it) over a page whose working revision holds
    /// a disclosure with its title in the attribute (3.5) and a page without one: the migration rewrites nothing and queues one run of the
    /// job; the job's rule today makes the disclosure a heading and its body (as 2026110995's). An installation without such a disclosure
    /// gets no run.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110960_queues_the_job_only_for_disclosure_titles_and_the_job_makes_them_headings()
    {
        const string Reason = "Aktualizace na verzi 3.6 (názvy rozbalovacích bloků)";
        const string Queued = $"""SELECT count(*) || ',' || coalesce(min("Reason"),'') FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued'""";
        var plain = """
            {"format":"atlas-rich","version":1,"plugins":[],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Bez bloku"}]}]}}
            """;
        var seed = $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Plain}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{PlainWorking}','{Plain}',1,'Bez bloku','{plain}','Bez bloku','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """;

        await using (var none = await DisposableSchema.CreateAsync("atlas_disclosure_titles_none_"))
        {
            await OutboxTests.ApplyBeforeAsync(none.ConnectionString, 2026110960);
            await OutboxTests.ExecuteAsync(none.ConnectionString, seed);
            await OutboxTests.ApplyBeforeAsync(none.ConnectionString, 2026110961, from: 2026110960);
            Assert.Equal("0,", await OutboxTests.ScalarAsync(none.ConnectionString, Queued));
        }

        await using var schema = await DisposableSchema.CreateAsync("atlas_disclosure_titles_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110960);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, seed + $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Deployment}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{Working}','{Deployment}',1,'Návod','{Shape35Disclosure("třetí")}','třetí','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """);
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110961, from: 2026110960);
        Assert.Equal("1," + Reason, await OutboxTests.ScalarAsync(schema.ConnectionString, Queued));
        Assert.Equal(Shape35Disclosure("třetí"), await OutboxTests.ScalarAsync(schema.ConnectionString,
            $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{Working}'"""));

        // The later migrations add no second run; the queued one converts the page and does not read the other one.
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal("1," + Reason, await OutboxTests.ScalarAsync(host.ConnectionString, Queued));
        var runner = host.Services.GetRequiredService<IJobRunner>();
        Assert.Equal("succeeded,1,1,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        Assert.Equal($$$"""
            {"format":"atlas-rich","version":1,"plugins":[],"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Podrobnosti třetí"}]},{"type":"paragraph","content":[{"type":"text","text":"třetí"}]}]}}
            """, await ContentAsync(host, Working));
        Assert.Equal(plain, await ContentAsync(host, PlainWorking));
    }

    /// <summary>Migration 2026110995 and the same job over pages that hold a disclosure (Atlas 3.6 has none: every heading folds): a page
    /// whose working, published and older history revisions hold one with its title in the attribute, a page of revision 6 (its title a
    /// node) with unsaved live edits, and a page without one. The run makes each disclosure a heading (its title, one level below the
    /// heading before it, 3 with none) and its body in the working and published revisions in place (author and time kept, the Markdown
    /// written again with the heading, so the title stays searchable), leaves history as stored, defers the live page and does not read the
    /// other one; once the live edits are saved, the next run converts that page too.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110995_turns_disclosures_into_headings_in_place_and_leaves_history_and_live_pages()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_no_disclosures_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110995);
        var plain = """
            {"format":"atlas-rich","version":1,"plugins":[],"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Hotovo"}]},{"type":"paragraph"}]}}
            """;
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Deployment}','{Space}','{Writer}',3), ('{Live}','{Space}','{Writer}',1), ('{Plain}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{History}','{Deployment}',1,'Návod','{Shape35Disclosure("první")}','první','{Writer}','2026-10-01T10:00:00Z','2026-10-02T10:00:00Z','web'),
              ('{Published}','{Deployment}',2,'Návod','{Shape35Disclosure("druhá")}','druhá','{Writer}','2026-10-02T10:00:00Z','2026-10-03T10:00:00Z','web'),
              ('{Working}','{Deployment}',3,'Návod','{Shape35Disclosure("třetí")}','třetí','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{LiveWorking}','{Live}',1,'Rozepsaná','{Shape36Disclosure("Živá")}','živá','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{PlainWorking}','{Plain}',1,'Bez bloku','{plain}','Hotovo','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='{Published}' WHERE "Id"='{Deployment}';
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',3,1,1,'2026-10-03T10:00:00Z');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal("1,Aktualizace na verzi 3.6 (rozbalovací bloky na nadpisy)", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) || ',' || min("Reason") FROM "JobRun" WHERE "JobId"='{RequirementsOverviewJob.JobId}' AND "State"='queued'
            """));
        var runner = host.Services.GetRequiredService<IJobRunner>();

        // Two pages hold a disclosure (the other one is not read): the working and published revisions are converted, the live page waits.
        Assert.Equal("succeeded,2,2,1,0", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        foreach (var (revision, text) in new[] { (Published, "druhá"), (Working, "třetí") })
        {
            var content = await ContentAsync(host, revision);
            Assert.Equal($$$"""
                {"format":"atlas-rich","version":1,"plugins":[],"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Podrobnosti {{{text}}}"}]},{"type":"paragraph","content":[{"type":"text","text":"{{{text}}}"}]}]}}
                """, content);
            Rules.ValidateDocument(content);
            Assert.Equal($"### Podrobnosti {text}\n\n{text}", (string)(await OutboxTests.ScalarAsync(host.ConnectionString,
                $"""SELECT "Markdown" FROM "PageRevision" WHERE "Id"='{revision}'"""))!);
        }
        Assert.Equal("1,2,3", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg("Version"::text, ',' ORDER BY "Version") FROM "PageRevision"
            WHERE "PageId"='{Deployment}' AND "AuthorId"='{Writer}' AND "ActiveFrom" = timestamptz '2026-10-01T10:00:00Z' + ("Version" - 1) * interval '1 day'
            """));
        Assert.Equal(Shape35Disclosure("první"), await ContentAsync(host, History));
        Assert.Equal(Shape36Disclosure("Živá"), await ContentAsync(host, LiveWorking));
        Assert.Equal(plain, await ContentAsync(host, PlainWorking));

        // Once the live edits are saved, the next run converts that page too and reads nothing else: its title one level below „Sekce“.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            UPDATE "PageCollaboration" SET "LastSavedSequence"="Sequence" WHERE "PageId"='{Live}';
            """);
        Assert.Equal("succeeded,1,1,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test")));
        Assert.Contains("""{"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Živá","marks":[{"type":"bold"}]}]},{"type":"paragraph"}""",
            await ContentAsync(host, LiveWorking), StringComparison.Ordinal);
    }

    /// <summary>The two 3.6 jobs are independent: on a 3.3 page (summary node and requirement blocks) either order ends at the same
    /// document, with requirement headings, the overview node and the current plugin version, valid under today's rules.</summary>
    [PostgreSqlFact]
    public async Task The_overview_and_headings_jobs_end_at_the_same_document_in_either_order()
    {
        const string OverviewFirst = "00000000-0000-0000-0000-0000000000d1", HeadingsFirst = "00000000-0000-0000-0000-0000000000d2",
            RevisionA = "00000000-0000-0000-0000-0000000000e1", RevisionB = "00000000-0000-0000-0000-0000000000e2";
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_order_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{OverviewFirst}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{RevisionA}','{OverviewFirst}',1,'A','{Shape33("text")}','text','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        var runner = host.Services.GetRequiredService<IJobRunner>();

        await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test");
        await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        var overviewFirst = await ContentAsync(host, RevisionA);

        // The second page appears only now, so the runs above did not see it; the jobs run in the opposite order on it.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{HeadingsFirst}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{RevisionB}','{HeadingsFirst}',1,'A','{Shape33("text")}','text','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """);
        await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test");
        var headingsFirst = await ContentAsync(host, RevisionB);

        Assert.Equal(overviewFirst, headingsFirst);
        var root = JsonNode.Parse(headingsFirst)!;
        var content = root["document"]!["content"]!.AsArray();
        Assert.Equal("overview", (string?)content[0]!["type"]);
        Assert.Equal(Summary, (string?)content[0]!["attrs"]!["id"]);
        Assert.Equal("heading", (string?)content[1]!["type"]);
        Assert.NotNull(content[1]!["attrs"]!["state"]);
        Assert.DoesNotContain("\"type\":\"requirement\"", headingsFirst);
        Assert.DoesNotContain("requirementSummary", headingsFirst);
        Assert.Equal(CollaborationSchema.CurrentPluginVersions["atlas-requirements"], (int)root["plugins"]![0]!["version"]!);
        Rules.ValidateDocument(headingsFirst);
    }

    /// <summary>4.0 first start over 3.5.2 content: every revision either job rewrites in place keeps its content from before the first
    /// rewrite in exactly one backup row, whichever job comes first: a 3.3 page the overview job rewrites before the headings job (its
    /// backup is not the overview's output), one the headings job rewrites first, a page with a disclosure only the overview job rewrites,
    /// and a page converted by the headings job while a page it depends on was backed up by the overview job but still holds blocks
    /// (converted, not refused: a backup alone does not make that page converted).</summary>
    [PostgreSqlFact]
    public async Task Every_revision_either_job_rewrites_keeps_its_original_in_one_backup_row_in_either_order()
    {
        const string Dependent = "00000000-0000-0000-0000-0000000000d0", OverviewFirst = "00000000-0000-0000-0000-0000000000d1",
            HeadingsFirst = "00000000-0000-0000-0000-0000000000d2", Disclosure = "00000000-0000-0000-0000-0000000000d3",
            RevisionDependent = "00000000-0000-0000-0000-0000000000e0", RevisionA = "00000000-0000-0000-0000-0000000000e1",
            RevisionB = "00000000-0000-0000-0000-0000000000e2", RevisionDisclosure = "00000000-0000-0000-0000-0000000000e3";
        var dependentJson = RequirementHeadingConverterTests.Envelope(RequirementHeadingConverterTests.Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6a0", "aplikace",
            "Aplikace", requires: $"vpn@{OverviewFirst}"));
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_backup_order_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Dependent}','{Space}','{Writer}',1), ('{OverviewFirst}','{Space}','{Writer}',1),
              ('{Disclosure}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{RevisionDependent}','{Dependent}',1,'D','{dependentJson}','d','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{RevisionA}','{OverviewFirst}',1,'A','{Shape33("text")}','a','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{RevisionDisclosure}','{Disclosure}',1,'C','{Shape35Disclosure("text")}','c','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        var runner = host.Services.GetRequiredService<IJobRunner>();

        await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test");
        await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        // The dependent page converts in the same run, before the page it depends on (page order), not in a later one.
        Assert.DoesNotContain("\"type\":\"requirement\"", await ContentAsync(host, RevisionDependent), StringComparison.Ordinal);
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{HeadingsFirst}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{RevisionB}','{HeadingsFirst}',1,'B','{Shape33("text")}','b','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            """);
        await runner.RunToEndAsync(RequirementsHeadingsJob.JobId, "Test");
        await runner.RunToEndAsync(RequirementsOverviewJob.JobId, "Test");

        foreach (var revision in new[] { RevisionDependent, RevisionA, RevisionB })
        {
            Assert.DoesNotContain("\"type\":\"requirement\"", await ContentAsync(host, revision), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("\"type\":\"disclosure\"", await ContentAsync(host, RevisionDisclosure), StringComparison.Ordinal);
        Assert.Equal("4,4", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) || ',' || count(o."Id") FROM "RequirementConversionBackup" b LEFT JOIN (VALUES
              (uuid '{RevisionDependent}', '{dependentJson}', 'd'), (uuid '{RevisionA}', '{Shape33("text")}', 'a'),
              (uuid '{RevisionB}', '{Shape33("text")}', 'b'), (uuid '{RevisionDisclosure}', '{Shape35Disclosure("text")}', 'c')) o("Id","Json","Markdown")
              ON o."Id" = b."RevisionId" AND o."Json" = b."ContentJson" AND o."Markdown" = b."Markdown" AND NOT b."RolledBack"
            """));
    }

    private static async Task<string> RunAsync(AtlasTestHost host, long runId) =>(string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""
        SELECT concat_ws(',', "State", "Progress"->>'pages', "Progress"->>'revisions', "Progress"->>'deferred', "Progress"->>'refused') FROM "JobRun" WHERE "Id"={runId}
        """))!;

    private static async Task<string> ContentAsync(AtlasTestHost host, string revision)
        => (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{revision}'"""))!;

    private static async Task<string> AllContentAsync(AtlasTestHost host) => (string)(await OutboxTests.ScalarAsync(host.ConnectionString,
        """SELECT string_agg("Id"::text || "ContentJson" || "Markdown", E'\n' ORDER BY "Id") FROM "PageRevision" """))!;
}
