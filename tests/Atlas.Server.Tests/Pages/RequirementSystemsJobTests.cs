using System.Text.Json.Nodes;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Validation;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>Migration 2026111150 and the job pages.requirement-systems over existing rows: a published catalog whose headings name
/// services, an order page (working and published revision, older history) whose requirements came from it without a service, with a
/// service of their own, from a catalog heading that names none and without a provenance, and pages with live editing. The migration
/// queues one run where a catalog is marked; the run fills only the empty services in place (author and time kept, history as stored,
/// nothing but the service changed, no conversion backup of its own), defers a page someone is typing on to a follow-up that reads that
/// page alone, and ends on a page nobody will ever save. The composed board of the filled page has the service cards.</summary>
public sealed class RequirementSystemsJobTests
{
    private const long Version = 2026111150;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
        Catalog = "00000000-0000-0000-0000-0000000000a1", Order = "00000000-0000-0000-0000-0000000000a2", Live = "00000000-0000-0000-0000-0000000000a3",
        CatalogPublished = "00000000-0000-0000-0000-0000000000b1", History = "00000000-0000-0000-0000-0000000000b2", Published = "00000000-0000-0000-0000-0000000000b3",
        Working = "00000000-0000-0000-0000-0000000000b4", LiveWorking = "00000000-0000-0000-0000-0000000000b5";

    private static string Heading(int level, string id, string text) =>
        $$$"""{"type":"heading","attrs":{"level":{{{level}}},"headingId":"{{{id}}}"},"content":[{"type":"text","text":"{{{text}}}"}]}""";

    private static string Requirement(int level, string id, string text, string system = "", string from = "") =>
        $$$"""{"type":"heading","attrs":{"level":{{{level}}},"headingId":"{{{id}}}","state":"planned","stateLabel":"","direction":"inbound","system":"{{{system}}}","from":"{{{from}}}","aliases":""},"content":[{"type":"text","text":"{{{text}}}"}]}""";

    private static string Rich(params string[] nodes) =>
        $$$"""{"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":4},{"id":"heading-anchors","version":1}],"document":{"type":"doc","content":[{{{string.Join(",", nodes)}}}]}}""";

    private static readonly string CatalogContent = Rich(Heading(2, "portal", "Portál pacienta"), Heading(3, "nia", "NIA"),
        Requirement(4, "nia-cert", "Certifikát NIA", "NIA"), Requirement(4, "sms", "Účet SMS brány", "SMS brána"), Requirement(4, "bez", "Bez služby"));

    // Text with an emoji and characters an encoder may escape, as the web editor stores them (raw UTF-8).
    private const string Para = """{"type":"paragraph","content":[{"type":"text","text":"Účet 😀 <b> & + – “x”"}]}""";

    /// <summary>An order page: its own section, then the portal with requirements from the catalog; <paramref name="nia"/> the service of the first.</summary>
    private static string OrderContent(string note, string nia = "") => Rich(Heading(2, "zakazka", "Zakázka"), Requirement(3, "dodatek", "Dodatek " + note), Para,
        Heading(2, "portal", "Portál pacienta"), Heading(3, "aut", "Autentizace"), Heading(4, "nia", "NIA"),
        Requirement(5, "nia-cert", "Certifikát NIA", nia, $"{Catalog}#nia-cert"),
        Requirement(5, "sms", "Účet SMS brány", "Vlastní brána", $"{Catalog}#sms"),
        Requirement(5, "bez", "Bez služby", from: $"{Catalog}#bez"),
        Requirement(5, "jiny", "Bez původu"));

    /// <summary>What the fill leaves of <see cref="OrderContent"/>: the one service, and the emoji as an escaped surrogate pair
    /// (RichDocument.JsonWrite writes it so; the same text to every reader).</summary>
    private static string Filled(string note) => OrderContent(note, "NIA").Replace(char.ConvertFromUtf32(0x1F600), string.Concat("\\", "u", "D83D", "\\", "u", "DE00"), StringComparison.Ordinal);

    /// <summary>The catalog, the order page (history, published, working) and a page with a live state: <paramref name="live"/> its row's
    /// sequence, saved sequence and last activity.</summary>
    private static async Task<AtlasTestHost> HostAsync(DisposableSchema schema, string live = "3,1,1,now()", string extra = "")
    {
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{Catalog}','{Space}','{Writer}',1), ('{Order}','{Space}','{Writer}',3), ('{Live}','{Space}','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('{CatalogPublished}','{Catalog}',1,'Katalog','{CatalogContent}','x','{Writer}','2026-10-01T10:00:00Z',NULL,'web'),
              ('{History}','{Order}',1,'Zakázka','{OrderContent("první")}','x','{Writer}','2026-10-01T10:00:00Z','2026-10-02T10:00:00Z','web'),
              ('{Published}','{Order}',2,'Zakázka','{OrderContent("druhý")}','x','{Writer}','2026-10-02T10:00:00Z','2026-10-03T10:00:00Z','web'),
              ('{Working}','{Order}',3,'Zakázka','{OrderContent("třetí")}','x','{Writer}','2026-10-03T10:00:00Z',NULL,'web'),
              ('{LiveWorking}','{Live}',1,'Rozepsaná','{OrderContent("živý")}','x','{Writer}','2026-10-03T10:00:00Z',NULL,'web');
            UPDATE "Page" SET "PublishedRevisionId"='{CatalogPublished}' WHERE "Id"='{Catalog}';
            UPDATE "Page" SET "PublishedRevisionId"='{Published}' WHERE "Id"='{Order}';
            UPDATE "Page" SET "PublishedRevisionId"='{LiveWorking}' WHERE "Id"='{Live}';
            INSERT INTO "ProjectorCatalogPage"("PageId","MarkedById","MarkedAt") VALUES('{Catalog}','{Writer}',now());
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Order}','00000000-0000-0000-0000-0000000000c1','{Working}',2,2,2,'2026-10-03T10:00:00Z'),
              ('{Live}','00000000-0000-0000-0000-0000000000c2','{LiveWorking}',{live});
            {extra}
            """);
        return await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
    }

    [PostgreSqlFact]
    public async Task Migration_2026111150_queues_the_fill_which_gives_empty_services_the_catalogs_and_keeps_everything_else()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirement_systems_");
        // The published revision was converted by pages.requirements-headings before: its pre-3.6 original is in the backup.
        await using var host = await HostAsync(schema, extra: $"""
            INSERT INTO "RequirementConversionBackup"("RevisionId","PageId","ContentJson","Markdown") VALUES('{Published}','{Order}','pre-3.6','pre-3.6 md');
            """);
        Assert.Equal("1,Aktualizace na verzi 4.0: služby požadavků z katalogu", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) || ',' || min("Reason") FROM "JobRun" WHERE "JobId"='{RequirementSystemsJob.JobId}' AND "State"='queued'
            """));
        var runner = host.Services.GetRequiredService<IJobRunner>();

        // Two pages hold a provenance: the order's working and published revisions take one service each; the page someone is typing on waits.
        Assert.Equal("succeeded,2,2,2,1,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementSystemsJob.JobId, "Test")));
        foreach (var (revision, note) in new[] { (Published, "druhý"), (Working, "třetí") })
        {
            // Nothing but the service changed: its own value wins, a catalog heading without a service and a requirement without a provenance give none.
            var content = await ContentAsync(host, revision);
            Assert.Equal(Filled(note), content);
            Rules.ValidateDocument(content);
            Assert.Contains("system=NIA", (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "Markdown" FROM "PageRevision" WHERE "Id"='{revision}'"""))!);
        }
        // In place: author, time and version stay; history, the catalog and the live page as stored.
        Assert.Equal("1,2,3", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg("Version"::text, ',' ORDER BY "Version") FROM "PageRevision"
            WHERE "PageId"='{Order}' AND "AuthorId"='{Writer}' AND "ActiveFrom" = timestamptz '2026-10-01T10:00:00Z' + ("Version" - 1) * interval '1 day'
            """));
        Assert.Equal(OrderContent("první"), await ContentAsync(host, History));
        Assert.Equal(OrderContent("živý"), await ContentAsync(host, LiveWorking));
        Assert.Equal(CatalogContent, await ContentAsync(host, CatalogPublished));
        // The conversion's backup keeps the 3.6 original and gets no row from the fill (it tells which pages the heading conversion converted).
        Assert.Equal("1,pre-3.6", await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) || ',' || min("ContentJson") FROM "RequirementConversionBackup" """));
        // The page's live state starts again from the filled revision and its published text is searchable again.
        Assert.Equal("0,false", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT "Sequence" || ',' || ("Generation" = '00000000-0000-0000-0000-0000000000c1') FROM "PageCollaboration" WHERE "PageId"='{Order}'
            """));
        Assert.Equal($"{Published}", (await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "RevisionId"::text FROM "SearchDocument" WHERE "PageId"='{Order}'"""))?.ToString());

        // The board composed from the filled page draws the services: NIA and the page's own, under the portal.
        Assert.Equal(["nia", "vlastni-brana"], DeploymentBoard.Of(JsonNode.Parse(await ContentAsync(host, Working))!["document"]).Services.Select(service => service.Anchor));
        Assert.DoesNotContain(DeploymentBoard.Of(JsonNode.Parse(OrderContent("třetí"))!["document"]).Services, service => service.Anchor == "nia");

        // The follow-up is for the deferred page alone: a service a person cleared on the filled page meanwhile stays cleared.
        Assert.Equal(1L, await FollowUpsAsync(host));
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            UPDATE "PageRevision" SET "ContentJson"='{OrderContent("třetí")}' WHERE "Id"='{Working}';
            UPDATE "PageCollaboration" SET "LastSavedSequence"="Sequence", "UpdatedAt"='2026-10-03T10:00:00Z' WHERE "PageId"='{Live}';
            """);
        Assert.Equal("succeeded,1,1,1,0,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementSystemsJob.JobId, "Test")));
        Assert.Equal(Filled("živý"), await ContentAsync(host, LiveWorking));
        Assert.Equal(OrderContent("třetí"), await ContentAsync(host, Working));
        Assert.Equal(0L, await FollowUpsAsync(host));

        // A run an administrator asks for reads every page again and fills what is empty; over filled content it changes nothing.
        await OutboxTests.ExecuteAsync(host.ConnectionString, """UPDATE "PageCollaboration" SET "UpdatedAt"='2026-10-03T10:00:00Z'""");
        Assert.Equal("succeeded,2,1,1,0,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementSystemsJob.JobId, "Test")));
        var filled = await AllContentAsync(host);
        Assert.Equal("succeeded,2,0,0,0,0,0", await RunAsync(host, await runner.RunToEndAsync(RequirementSystemsJob.JobId, "Test")));
        Assert.Equal(filled, await AllContentAsync(host));
    }

    /// <summary>Unsaved live edits abandoned weeks ago: nobody types, nobody will save them. The page is deferred once and the job ends;
    /// it does not come back every ten minutes (review, 2026-11-11).</summary>
    [PostgreSqlFact]
    public async Task A_page_with_abandoned_unsaved_live_edits_does_not_keep_the_job_running()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirement_systems_abandoned_");
        await using var host = await HostAsync(schema, live: "3,1,1,'2026-10-03T10:00:00Z'");
        var runner = host.Services.GetRequiredService<IJobRunner>();
        var run = await RunAsync(host, await runner.RunToEndAsync(RequirementSystemsJob.JobId, "Test"));
        Assert.StartsWith("succeeded,2,", run, StringComparison.Ordinal);
        Assert.Equal(Filled("třetí"), await ContentAsync(host, Working));
        Assert.Equal(0L, await FollowUpsAsync(host));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementSystemsJob.JobId}' AND "State"='queued'"""));
    }

    /// <summary>The provenance is written by pages.requirements-headings: while it has a run waiting, the fill touches nothing and comes back later.</summary>
    [PostgreSqlFact]
    public async Task Nothing_is_filled_while_the_heading_conversion_waits()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirement_systems_waits_");
        await using var host = await HostAsync(schema);
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "JobRun"("JobId","Trigger","Reason","State","NotBefore") VALUES ('{RequirementsHeadingsJob.JobId}','event','Test','queued', now() + interval '1 day')
            """);
        var before = await AllContentAsync(host);
        Assert.Equal("succeeded,0,0,0,0,0,0", await RunAsync(host, await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementSystemsJob.JobId, "Test")));
        Assert.Equal(before, await AllContentAsync(host));
        Assert.Equal(1L, await FollowUpsAsync(host));
    }

    /// <summary>Only a published catalog page that is not in the trash gives services.</summary>
    [PostgreSqlTheory]
    [InlineData("""UPDATE "Page" SET "PublishedRevisionId"=NULL WHERE "Id"='00000000-0000-0000-0000-0000000000a1'""")]
    [InlineData("""UPDATE "Page" SET "IsTrashed"=true, "TrashedAt"=now(), "TrashedById"='00000000-0000-0000-0000-000000000001', "TrashBatchId"=gen_random_uuid() WHERE "Id"='00000000-0000-0000-0000-0000000000a1'""")]
    public async Task An_unpublished_or_trashed_catalog_fills_nothing(string catalog)
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirement_systems_catalog_");
        await using var host = await HostAsync(schema, live: "1,1,1,'2026-10-03T10:00:00Z'", extra: catalog + ";");
        var before = await AllContentAsync(host);
        Assert.Equal("succeeded,2,0,0,0,0,0", await RunAsync(host, await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementSystemsJob.JobId, "Test")));
        Assert.Equal(before, await AllContentAsync(host));
    }

    [PostgreSqlFact]
    public async Task The_migration_queues_nothing_without_a_catalog_and_once_when_its_script_runs_again()
    {
        const string Queued = $"""SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementSystemsJob.JobId}' AND "State"='queued'""";
        await using var empty = await DisposableSchema.CreateAsync("atlas_requirement_systems_none_");
        await using (var none = await AtlasTestHost.CreateAsync(empty, modules: TestModules.AllEnabled))
        {
            Assert.Equal(0L, await OutboxTests.ScalarAsync(none.ConnectionString, Queued));
        }
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirement_systems_again_");
        await using var host = await HostAsync(schema);
        var assembly = typeof(AtlasDatabase).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(x => x.EndsWith("2026111150_requirement_systems.sql", StringComparison.Ordinal)))!;
        await OutboxTests.ExecuteAsync(host.ConnectionString, await new StreamReader(stream).ReadToEndAsync());
        Assert.Equal(1L, await OutboxTests.ScalarAsync(host.ConnectionString, Queued));
    }

    private static Task<object?> FollowUpsAsync(AtlasTestHost host) => OutboxTests.ScalarAsync(host.ConnectionString, $"""
        SELECT count(*) FROM "JobRun" WHERE "JobId"='{RequirementSystemsJob.JobId}' AND "State"='queued' AND "NotBefore" > now()
        """);

    private static async Task<string> RunAsync(AtlasTestHost host, long runId) => (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""
        SELECT concat_ws(',', "State", "Progress"->>'pages', "Progress"->>'revisions', "Progress"->>'filled', "Progress"->>'deferred', "Progress"->>'refused', "Progress"->>'failed') FROM "JobRun" WHERE "Id"={runId}
        """))!;

    private static async Task<string> ContentAsync(AtlasTestHost host, string revision)
        => (string)(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "Id"='{revision}'"""))!;

    private static async Task<string> AllContentAsync(AtlasTestHost host) => (string)(await OutboxTests.ScalarAsync(host.ConnectionString,
        """SELECT string_agg("Id"::text || "ContentJson" || "Markdown", E'\n' ORDER BY "Id") FROM "PageRevision" """))!;
}
