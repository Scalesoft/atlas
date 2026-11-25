using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Features.Projector.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>Projector's measured tests: they time statements, so they run alone after the parallel ones and do not slow those down
/// (ApiActivityTests' rate-limit window is one of them).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProjectorPerformanceCollection
{
    public const string Name = "Projector performance";
}

/// <summary>
/// The space at the size of a real wiki (owner's request for R4), seeded once for the facts of <see cref="ProjectorPerformanceTests"/>
/// (review C2-15): 300 deployment pages × 40 blocks between paragraphs and a catalog of 10 pages × 50 definitions, seeded in SQL, beside
/// a space of one page and one catalog, and a 500-block page. Created on first use, so the class skips cleanly without a database.
/// </summary>
public sealed class ProjectorPerformanceSeed : IAsyncLifetime
{
    public const int Pages = 300, BlocksPerPage = 40, CatalogPages = 10, DefinitionsPerCatalog = 50, BigPageBlocks = 500;

    private readonly SemaphoreSlim m_gate = new(1, 1);
    private ProjectorFixture? m_fixture;

    public Guid Small { get; private set; }
    public Guid SmallPage { get; private set; }
    public Guid BigPage { get; private set; }
    public TimeSpan Seeding { get; private set; }

    internal async Task<ProjectorFixture> FixtureAsync()
    {
        await m_gate.WaitAsync();
        try
        {
            if (m_fixture is null)
            {
                var f = await ProjectorFixture.CreateAsync();
                Small = await f.Host.SeedSpaceAsync("Malý", (f.Writer, SpaceRole.Writer));
                var seeding = Stopwatch.StartNew();
                await SeedAsync(f, Small, pages: 1, catalogs: 1);
                await SeedAsync(f, f.SpaceId, Pages, CatalogPages);
                Seeding = seeding.Elapsed;
                SmallPage = await CreateAsync(f, Small, "Malá stránka", Content("p", 1));
                BigPage = await CreateAsync(f, f.SpaceId, "Velká stránka", Content("p", BigPageBlocks));
                m_fixture = f;
            }
            return m_fixture;
        }
        finally
        {
            m_gate.Release();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (m_fixture is not null)
        {
            await m_fixture.DisposeAsync();
        }
        m_gate.Dispose();
    }

    /// <summary>Pages with published content and their catalog marks in a few statements; the index is built by its triggers at commit.</summary>
    internal static async Task SeedAsync(ProjectorFixture f, Guid space, int pages, int catalogs)
    {
        await f.Host.SeedAsync(async session =>
        {
            await InsertPagesAsync(session, space, f.Writer.Id, "Nasazení", Content("n", BlocksPerPage), pages, catalog: false);
            for (var catalog = 0; catalog < catalogs; catalog++)
            {
                var definitions = Json(Enumerable.Range(0, DefinitionsPerCatalog).Select(i => (JsonNode)Block(Id($"c{catalog}", i), $"cat{catalog}.k{i}",
                    $"Definice {catalog}/{i}", requires: i % 2 == 0 && i + 1 < DefinitionsPerCatalog ? $"cat{catalog}.k{i + 1}" : "")).ToArray());
                await InsertPagesAsync(session, space, f.Writer.Id, $"Katalog {catalog}", definitions, 1, catalog: true);
            }
            await session.CreateSQLQuery("""
                    UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."SpaceId"=:space AND p."PublishedRevisionId" IS NULL
                    """).SetParameter("space", space).ExecuteUpdateAsync();
        });
    }

    private static Task InsertPagesAsync(NHibernate.ISession session, Guid space, Guid owner, string title, string json, int count, bool catalog)
    {
        return session.CreateSQLQuery("""
                WITH seed AS MATERIALIZED (
                  SELECT gen_random_uuid() AS "PageId",gen_random_uuid() AS "RevisionId",:title || ' ' || lpad(CAST(n AS text),3,'0') AS "Title" FROM generate_series(1,:count) n
                ), pages AS (
                  INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") SELECT "PageId",:space,:owner,1 FROM seed
                ), revisions AS (
                  INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
                  SELECT "RevisionId","PageId",1,"Title",:json,'',:owner,now(),'web' FROM seed
                )
                INSERT INTO "ProjectorCatalogPage"("PageId","MarkedById","MarkedAt") SELECT "PageId",:owner,clock_timestamp() FROM seed WHERE :catalog
                """)
            .SetParameter("title", title).SetParameter("count", count).SetParameter("space", space).SetParameter("owner", owner)
            .SetParameter("json", json).SetParameter("catalog", catalog).ExecuteUpdateAsync();
    }

    internal static async Task<Guid> CreateAsync(ProjectorFixture f, Guid space, string title, string json)
    {
        var page = await f.Pages.CreateAsync(new CreatePageRequest(space, null, title, json, null), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        return page.Id;
    }

    private static string States(int i) => (i % 6) switch { 0 => "done", 1 => "waiting_for_input", 2 => "in_progress", 3 => "planned", 4 => "blocked", _ => "not_applicable" };

    /// <summary>A deployment page as people write one: each block with a note paragraph after it.</summary>
    internal static string Content(string prefix, int count) => Json(Enumerable.Range(0, count).SelectMany(i => new JsonNode[]
    {
        Block(Id(prefix, i), $"k{i}", $"Požadavek {i}", state: States(i), direction: i % 3 == 0 ? "inbound" : "internal", party: $"Strana {i % 7}",
            requires: i > 0 ? $"k{i - 1}" : ""),
        Paragraph($"Poznámka k požadavku {i}: kde je co uloženo a kdo to dodá.")
    }).ToArray());

    /// <summary>Paragraphs only, about <paramref name="length"/> characters of stored content.</summary>
    internal static string Plain(int length) => Json(Enumerable.Range(0, length / 80).Select(i => (JsonNode)Paragraph($"Odstavec {i:D5} prostého textu stránky bez požadavků, jen poznámky.")).ToArray());

    /// <summary>A UUID-shaped block id unique per prefix and index.</summary>
    internal static string Id(string prefix, int index)
    {
        var hash = (uint)StringComparer.Ordinal.GetHashCode(prefix);
        return $"{hash:x8}-0000-4000-8000-{index:x12}";
    }
}

/// <summary>
/// Projector at the size of a real wiki (<see cref="ProjectorPerformanceSeed"/>). Each read and the insertion into a 500-block page take as
/// many SQL statements as in a space of one page (no N+1), the space reads fetch only the rows they show, and the wall times are written
/// to the test output. The index triggers are measured on their own: a save of a page without requirements, and page updates that leave
/// the publication.
/// </summary>
[Collection(ProjectorPerformanceCollection.Name)]
public sealed class ProjectorPerformanceTests(ProjectorPerformanceSeed seed, ITestOutputHelper output) : IClassFixture<ProjectorPerformanceSeed>
{
    private const int Pages = ProjectorPerformanceSeed.Pages, BlocksPerPage = ProjectorPerformanceSeed.BlocksPerPage, BigPageBlocks = ProjectorPerformanceSeed.BigPageBlocks;
    private const int Definitions = ProjectorPerformanceSeed.CatalogPages * ProjectorPerformanceSeed.DefinitionsPerCatalog;

    private static readonly string[] Wanted = Enumerable.Range(0, 20)
        .Select(i => $"cat{i % ProjectorPerformanceSeed.CatalogPages}.k{i * 2 % ProjectorPerformanceSeed.DefinitionsPerCatalog}").ToArray();

    private static readonly string[] SmallWanted = ["cat0.k0"];

    /// <summary>P8, P2-6: the catalog is built from JSON once per publication (cold), then read from memory (warm) for every reader,
    /// and a warm read fetches no page content, only a hash of it.</summary>
    [PostgreSqlFact]
    public async Task A_warm_catalog_read_fetches_no_page_content_and_shares_the_definitions_between_readers()
    {
        var f = await seed.FixtureAsync();
        // A revision changed in place (an import) is a miss of its page: every catalog page of the space is cold now.
        await f.Host.ExecuteAsync("""
            UPDATE "PageRevision" r SET "ContentJson"=r."ContentJson" || ' ' FROM "ProjectorCatalogPage" c JOIN "Page" p ON p."Id"=c."PageId"
            WHERE r."Id"=p."PublishedRevisionId" AND p."SpaceId"=:id
            """, f.SpaceId);

        var (coldStatements, cold) = await RecordedAsync(() => f.As<ProjectorCatalog>(f.Writer, new GetProjectorCatalog(f.SpaceId)));
        var (warmStatements, warm) = await RecordedAsync(() => f.As<ProjectorCatalog>(f.Writer, new GetProjectorCatalog(f.SpaceId)));
        var writers = await f.As<ProjectorCatalog>(f.Writer, new GetProjectorCatalog(f.SpaceId));
        var readers = await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId));
        output.WriteLine($"catalog of {Definitions} definitions: cold {cold:F1} ms, warm {warm:F1} ms");

        Assert.Contains(coldStatements, ReadsContent);
        Assert.DoesNotContain(warmStatements, ReadsContent);
        Assert.Same(writers.Definitions[0].ContentJson, readers.Definitions[0].ContentJson);
    }

    [PostgreSqlFact]
    public async Task Space_reads_take_the_statements_of_a_small_space()
    {
        var f = await seed.FixtureAsync();
        var reads = new (string Name, Func<Guid, object> Message)[]
        {
            ("overview all", space => new GetProjectorOverview(space, "all")),
            ("overview missing", space => new GetProjectorOverview(space, "missing")),
            ("waiting", space => new GetProjectorWaiting(space)),
            ("catalog", space => new GetProjectorCatalog(space)),
            ("resolve 20 keys", space => new ResolveProjectorRequirements(space, space == seed.Small ? SmallWanted : Wanted, [])),
        };
        output.WriteLine($"seeding {Pages} pages with the index built at commit: {seed.Seeding.TotalSeconds:F1} s");
        foreach (var (name, message) in reads)
        {
            var (smallCount, _) = await MeasureAsync<object>(f, message(seed.Small));
            var (bigCount, time) = await MeasureAsync<object>(f, message(f.SpaceId));
            output.WriteLine($"{name}: {bigCount} statements (small space {smallCount}), {time:F1} ms");
            Assert.Equal(smallCount, bigCount);
        }
    }

    /// <summary>P7: the space reads fetch only the rows they show. Of every 6 blocks one is waiting and four are not finished.</summary>
    [PostgreSqlFact]
    public async Task Space_reads_fetch_only_the_rows_they_show()
    {
        var f = await seed.FixtureAsync();
        using var session = f.Host.OpenSession();
        var index = new RequirementIndex(session);

        var waiting = await index.SpaceAsync(f.Writer, f.SpaceId, "waiting", default);
        var missing = await index.SpaceAsync(f.Writer, f.SpaceId, "missing", default);

        output.WriteLine($"rows fetched: waiting {waiting.Count}, missing {missing.Count} of {Pages * BlocksPerPage + Definitions + BigPageBlocks} indexed");
        Assert.Equal(Pages * Count(BlocksPerPage, i => i % 6 == 1) + Count(BigPageBlocks, i => i % 6 == 1), waiting.Count);
        Assert.All(missing, row => Assert.DoesNotContain(row.Requirement.State, new[] { "done", "not_applicable" }));
    }

    [PostgreSqlFact]
    public async Task An_insertion_into_a_500_block_page_takes_the_statements_of_one_into_a_1_block_page()
    {
        var f = await seed.FixtureAsync();
        var smallVersion = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(seed.SmallPage))).Version;
        var bigVersion = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(seed.BigPage))).Version;

        var (smallInsert, _) = await MeasureAsync<RequirementInsertResult>(f, new InsertRequirements(seed.SmallPage, SmallWanted, new RequirementPosition(), smallVersion));
        var (bigInsert, insertTime) = await MeasureAsync<RequirementInsertResult>(f, new InsertRequirements(seed.BigPage, Wanted, new RequirementPosition(), bigVersion));

        output.WriteLine($"insert 20 keys into a {BigPageBlocks}-block page: {bigInsert} statements (1-block page {smallInsert}), {insertTime:F1} ms");
        Assert.Equal(smallInsert, bigInsert);
    }

    /// <summary>The requirement index in code (migration 2026110870): the check a read makes before it reads an indexed 500-block page
    /// (owner's bound: 20 ms, as the trigger's refresh had), and the refresh of that page, what the outbox's delivery after a save costs
    /// (written out).</summary>
    [PostgreSqlFact]
    public async Task The_freshness_check_of_an_indexed_500_block_page_stays_within_20_ms()
    {
        var f = await seed.FixtureAsync();
        using var session = f.Host.OpenSession();
        await ProjectorIndex.EnsureFreshAsync(session, [seed.BigPage], default);
        var (check, refresh) = (double.MaxValue, double.MaxValue);
        for (var round = 0; round < 3; round++)
        {
            var clock = Stopwatch.StartNew();
            await ProjectorIndex.EnsureFreshAsync(session, [seed.BigPage], default);
            check = Math.Min(check, clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            using (var transaction = session.BeginTransaction())
            {
                await ProjectorIndex.RefreshAsync(session, [seed.BigPage], default);
                await transaction.CommitAsync();
            }
            refresh = Math.Min(refresh, clock.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"freshness check of the indexed big page: {check:F1} ms; its refresh (what a save's outbox delivery costs): {refresh:F1} ms");
        Assert.True(check < 20, $"check {check:F1} ms");
    }

    /// <summary>A block update of the big page against a plain save of a page of the same size (written out, not bounded).</summary>
    [PostgreSqlFact]
    public async Task A_block_update_of_the_big_page_is_measured_against_a_plain_save_as_large()
    {
        var f = await seed.FixtureAsync();
        var smallVersion = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(seed.SmallPage))).Version;
        var bigVersion = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(seed.BigPage))).Version;
        var block = ProjectorPerformanceSeed.Id("p", 0);
        await f.As<RequirementWriteResult>(f.Writer, new UpdateRequirement(seed.SmallPage, block, new RequirementChange(State: "in_progress"), smallVersion));

        var (_, updateTime) = await MeasureAsync<RequirementWriteResult>(f, new UpdateRequirement(seed.BigPage, block, new RequirementChange(State: "in_progress"), bigVersion));
        var size = ProjectorPerformanceSeed.Content("p", BigPageBlocks).Length;
        var plain = await ProjectorPerformanceSeed.CreateAsync(f, f.SpaceId, "Prostá stránka " + Guid.NewGuid(), ProjectorPerformanceSeed.Plain(size));
        var plainPage = await f.Pages.GetAsync(plain, f.Writer, default);
        var plainTime = Stopwatch.StartNew();
        await f.Pages.SaveAsync(plain, new SavePageRequest(plainPage.Title, ProjectorPerformanceSeed.Plain(size + 80), null, plainPage.Version), f.Writer, default);

        output.WriteLine($"update one block of the big page (save + index at commit): {updateTime:F1} ms; a plain save of a page as large: {plainTime.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>The page read of the index goes through its primary key. The space join is written out only: at this size the planner
    /// rightly prefers one hash join over a scan of the whole table to a primary-key probe per page, so its choice is no regression signal.</summary>
    [PostgreSqlFact]
    public async Task The_page_read_of_the_index_uses_its_primary_key()
    {
        var f = await seed.FixtureAsync();

        var plan = await PlanAsync(f, """SELECT * FROM "ProjectorRequirementIndex" WHERE "PageId"=@page AND "IsWorking" ORDER BY "Position" """, seed.BigPage);

        output.WriteLine("page plan:\n" + plan);
        Assert.Contains("PK_ProjectorRequirementIndex", plan);
    }

    /// <summary>P1: the refresh of a 500 KB page without requirements reads its revisions and finds nothing to parse (no requirement block,
    /// no state). The bound was 5 ms for the trigger's walk inside one SQL call; the refresh in code takes its own round trips (the lock, the
    /// 500 KB of the revisions over the wire, the delete and the insert) and runs after the save's commit from the outbox, not inside the
    /// save, so 50 ms is its bound: next to nothing for a save. P2 (page updates that keep the publication did not refresh the index) went
    /// with the trigger: no write runs anything of the index now, so there is nothing left to bound there.</summary>
    [PostgreSqlFact]
    public async Task The_index_of_a_large_page_without_requirements_costs_a_save_almost_nothing()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await ProjectorPerformanceSeed.CreateAsync(f, f.SpaceId, "Velká prostá stránka", ProjectorPerformanceSeed.Plain(500_000));
        using var session = f.Host.OpenSession();
        var refresh = double.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var clock = Stopwatch.StartNew();
            using var transaction = session.BeginTransaction();
            await ProjectorIndex.RefreshAsync(session, [page], default);
            await transaction.CommitAsync();
            refresh = Math.Min(refresh, clock.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"index refresh of a 500 KB page without requirements: {refresh:F2} ms");
        Assert.True(refresh < 50, $"refresh {refresh:F2} ms");
    }

    /// <summary>The freshness check across every page, which „Co to blokuje“ (GetRequirementDependentsHandler) makes before it reads:
    /// over the seeded instance (300 pages of 40 blocks, 10 catalogs, a 500-block page), all indexed, it is one statement within 50 ms.</summary>
    [PostgreSqlFact]
    public async Task The_freshness_check_of_every_page_is_one_statement_within_50_ms()
    {
        var f = await seed.FixtureAsync();
        using var session = f.Host.OpenSession();
        await ProjectorIndex.EnsureFreshAsync(session, null, default);
        var (statements, best) = (0, double.MaxValue);
        for (var round = 0; round < 3; round++)
        {
            var (recorded, time) = await RecordedAsync(async () => { await ProjectorIndex.EnsureFreshAsync(session, null, default); return 0; });
            (statements, best) = (recorded.Count, Math.Min(best, time));
        }

        output.WriteLine($"freshness check of every page of the seeded instance: {statements} statement(s), {best:F1} ms");
        Assert.Equal(1, statements);
        Assert.True(best < 50, $"check {best:F1} ms");
    }

    /// <summary>A statement that selects a page's content itself; its md5 alone is no content read.</summary>
    private static bool ReadsContent(string statement) => Regex.IsMatch(statement, @"(?<!md5\(r\.)""ContentJson""");

    private static async Task<(int Statements, double Milliseconds)> MeasureAsync<T>(ProjectorFixture f, object message)
    {
        if (message is not (InsertRequirements or UpdateRequirement))
        {
            // Warm: the first call of a message type generates its handler. A write is measured second (the small page warms it).
            await f.As<object>(f.Writer, message);
        }
        var (statements, time) = await RecordedAsync(() => f.As<T>(f.Writer, message));
        return (statements.Count, time);
    }

    private static async Task<(IReadOnlyList<string> Statements, double Milliseconds)> RecordedAsync<T>(Func<Task<T>> call)
    {
        using var recording = SqlRecording.Start();
        var time = Stopwatch.StartNew();
        await call();
        return (recording.Statements, time.Elapsed.TotalMilliseconds);
    }

    private static int Count(int blocks, Func<int, bool> state) => Enumerable.Range(0, blocks).Count(state);

    private static async Task<string> PlanAsync(ProjectorFixture f, string sql, Guid page)
    {
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var lines = await session.CreateSQLQuery("EXPLAIN " + sql.Replace("@page", ":page", StringComparison.Ordinal))
            .SetParameter("page", page).ListAsync<string>();
        await transaction.RollbackAsync();
        return string.Join("\n", lines);
    }
}
