using System.Diagnostics;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Features.Contacts.LinkRun.Queries;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Relations;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.Contacts;

/// <summary>
/// The link migration's report stays a synchronous admin request (contacts spec §6) only while it is fast: a wiki of 2 000 published
/// pages, each with two contacts written without objectId, 300 people among them, every one new to the directory. The report and
/// apply's start (the run's row) are bounded by the clock; the run's rounds (its plan with the 300 new contacts, one batch) are written out.
/// </summary>
[Collection(RelationsPerformanceCollection.Name)]
public sealed class ContactLinkBudgetTests(ITestOutputHelper output)
{
    private const int Pages = 2000;
    private const int People = 300;
    private const int Runs = 3;
    private const double ReportBudgetMs = 900;
    private const double StartBudgetMs = 900;

    [PostgreSqlFact]
    public async Task The_report_of_a_large_wiki_answers_within_the_budget()
    {
        // Own host: the report and the link run read every page of the wiki (asserted counts are the whole wiki's), and the run is one
        // per installation (GetContactLinkRun), as in ContactLinkRunTests.
        await using var f = await RelationsFixture.CreateAsync();
        await ContactLinkRunTests.GrantEveryoneAsync(f);
        var (space, owner) = (f.SpaceId, f.Writer.Id);
        string Contact(string n) =>
            """{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Osoba ' || """ + n + """ || '","emails":["osoba' || """ + n
            + """ || '@example.cz"]}]}}}""";
        var content = """'{"format":"atlas-rich","version":1,"plugins":[{"id":"pages","version":1},{"id":"contacts","version":2}],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Za provoz "},'"""
            + " || '" + Contact($"(n % {People})") + "," + Contact($"((n + 7) % {People})") + """]}]}}'""";
        await ContentsBudgetTests.SqlAsync(f, $$"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT md5('page' || n)::uuid, '{{space}}', '{{owner}}', 1 FROM generate_series(1, {{Pages}}) n;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT md5('revision' || n)::uuid, md5('page' || n)::uuid, 1, 'Stránka ' || n, {{content}}, 'Text', '{{owner}}', now(), 'web'
              FROM generate_series(1, {{Pages}}) n;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."SpaceId"='{{space}}';
            ANALYZE;
            """);

        ContactLinkReport report = null!;
        var times = new List<double>();
        for (var run = 0; run < Runs + 1; run++)
        {
            var watch = Stopwatch.StartNew();
            report = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
            watch.Stop();
            if (run > 0)
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        Assert.Equal((Pages, Pages, People), (report.Pages, report.Revisions, report.Created.Count));
        output.WriteLine($"report: median {times.Order().ElementAt(Runs / 2):0} ms, max {times.Max():0} ms");

        var start = Stopwatch.StartNew();
        var started = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        var startMs = start.Elapsed.TotalMilliseconds;
        output.WriteLine($"apply's start (the request): {startMs:0} ms");
        // The plan's rounds (a batch of pages, then a batch of new contacts, each its own transaction) until the run's report holds the plan.
        var prepare = Stopwatch.StartNew();
        var rounds = 0;
        do
        {
            Assert.Equal(1, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(started.Id))).Pages);
            rounds++;
        }
        while ((await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!.Report.Pages == 0);
        output.WriteLine($"the run's plan, {People} new contacts in {rounds} rounds: {prepare.Elapsed.TotalMilliseconds:0} ms");
        var batch = Stopwatch.StartNew();
        Assert.Equal(ContactLinkMigration.BatchPages, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(started.Id))).Pages);
        output.WriteLine($"one batch of {ContactLinkMigration.BatchPages} pages: {batch.Elapsed.TotalMilliseconds:0} ms");

        // 2026-10-30: report median 402 ms, max 439 ms; about twice that as the guard, the start (since 3.6 only the run's row) alike.
        Assert.True(times.Max() <= ReportBudgetMs, $"report {times.Max():0} ms > {ReportBudgetMs} ms");
        Assert.True(startMs <= StartBudgetMs, $"apply's start {startMs:0} ms > {StartBudgetMs} ms");
    }
}
