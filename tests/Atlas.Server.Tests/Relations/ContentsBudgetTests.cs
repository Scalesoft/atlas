using System.Diagnostics;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Features.Relations.Contracts;
using NHibernate;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.Relations;

/// <summary>Timed tests run alone after the parallel ones, as Projector's measured tests do.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RelationsPerformanceCollection
{
    public const string Name = "Relations performance";
}

/// <summary>
/// The time budget of spec §7 (p95 150 ms) for the contents of a space of 5 000 objects and 20 000 edges, read by a Reader (the page
/// rights of EffectivePageAccess, not the WikiAdmin shortcut). Bounded is what makes the time on any host (PERF3-4, as
/// EntitiesBudgetTests): the executed plan of each statement that reads them touches about as many pages as today. The milliseconds,
/// of the database and of the handler, are written out, not bounded; the statement count is PerformanceBudgetHttpTests'.
/// </summary>
[Collection(RelationsPerformanceCollection.Name)]
public sealed class ContentsBudgetTests(ITestOutputHelper output)
{
    private const int Pages = 2000;
    private const int Boards = 1000;
    private const int Contacts = 2000;
    private const int Runs = 20;

    [PostgreSqlFact]
    public async Task Five_thousand_objects_and_twenty_thousand_edges_list_within_the_budget()
    {
        // Own host: the bounds are the shared buffers the links, placements, mentions and nodes statements read, calibrated on tables
        // of only this test's rows; on the shared host the mentions statement read 374 buffers against a bound of 183.
        await using var f = await RelationsFixture.CreateAsync();
        var (space, owner) = (f.SpaceId, f.Writer.Id);
        // 2 000 published pages, 1 000 boards, 2 000 contacts; each page links to five others and mentions a contact, each board holds
        // eight cards: 10 000 links, 8 000 placements, 2 000 mentions.
        await SqlAsync(f, $$"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT md5('page' || n)::uuid, '{{space}}', '{{owner}}', 1 FROM generate_series(1, {{Pages}}) n;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT md5('revision' || n)::uuid, md5('page' || n)::uuid, 1, 'Stránka ' || n, '{{RelationsFixture.Rich()}}', 'Text', '{{owner}}', now(), 'web'
              FROM generate_series(1, {{Pages}}) n;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."SpaceId"='{{space}}';
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder")
              SELECT md5('board' || n)::uuid, '{{space}}', 'Tabule ' || n, 1, now(), '{{owner}}', now(), false, 1024 * n FROM generate_series(1, {{Boards}}) n;
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","CreatedAt","Title","Data")
              SELECT md5('contact' || n)::uuid, 'atlas.contact', 'space', '{{space}}', now(), 'Kontakt ' || n, jsonb_build_object('name', 'Kontakt ' || n)
              FROM generate_series(1, {{Contacts}}) n;
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","TargetResourceId","Version","IsDeleted","PropsJson")
              SELECT md5('card' || b || '-' || c)::uuid, md5('board' || b)::uuid, CASE WHEN c % 2 = 0 THEN 'page' ELSE 'object' END, 0, 0, 240, 160, c,
                CASE WHEN c % 2 = 0 THEN md5('page' || (1 + (b * 8 + c) % {{Pages}}))::uuid ELSE md5('contact' || (1 + (b * 8 + c) % {{Contacts}}))::uuid END,
                1, false, CASE WHEN c % 2 = 0 THEN '{"display":"card"}' ELSE '{"view":"card"}' END::jsonb
              FROM generate_series(1, {{Boards}}) b CROSS JOIN generate_series(1, 8) c;
            {{Atlas.Server.Tests.Platform.DerivedRows.Sql}}
            """);
        // Both projections are filled directly: no trigger derives them from the (empty) content above.
        await SqlAsync(f, $$"""
            INSERT INTO "PageLink"("SourcePageId","TargetPageId","SourceRevisionId")
              SELECT md5('page' || n)::uuid, md5('page' || (1 + (n + k * 37) % {{Pages}}))::uuid, md5('revision' || n)::uuid
              FROM generate_series(1, {{Pages}}) n CROSS JOIN generate_series(1, 5) k;
            INSERT INTO "PageObjectMention"("SourcePageId","ObjectId","SourceRevisionId")
              SELECT md5('page' || n)::uuid, md5('contact' || n)::uuid, md5('revision' || n)::uuid FROM generate_series(1, {{Pages}}) n;
            ANALYZE;
            """);

        SpaceContents contents = null!;
        var times = new List<double>();
        for (var run = 0; run < Runs + 3; run++)
        {
            var watch = Stopwatch.StartNew();
            contents = await f.ContentsAsync(f.Reader, sort: run % 2 == 0 ? "title" : "used");
            watch.Stop();
            if (run >= 3)
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        times.Sort();
        var p95 = times[(int)Math.Ceiling(Runs * 0.95) - 1];
        output.WriteLine($"contents of {Pages + Boards + Contacts} objects, handler wall clock: median {times[Runs / 2]:0} ms, p95 {p95:0} ms, max {times[^1]:0} ms");
        Assert.Equal(Pages + Boards + Contacts, contents.Summary.Types.Values.Sum());
        // Every page is linked and every contact mentioned; nothing points at the boards.
        Assert.Equal(new RelationCounts(0, Boards, 0, 0), contents.Summary.Relations);

        // The statements of one contents read, with the handler's parameters: one per relation source, then the nodes of the space and
        // of every edge's ends.
        var whole = new Dictionary<string, object> { ["whole"] = true, ["space"] = space, ["around"] = "" };
        var sources = new[] { PageLinkRelations.Sql, BoardRelations.Sql, ContactMentionRelations.Sql };
        // The handler asks for drafts: they keep an object from being unused (ListSpaceContentsHandler).
        var statements = sources.Select(sql => (RelationAnchor.Cte + sql, sql == ContactMentionRelations.Sql ? new Dictionary<string, object>(whole) { ["drafts"] = true } : whole)).ToList();
        using (var session = f.Host.OpenSession())
        {
            var ends = new List<Guid>();
            // Drafts as the handler asks: the mention source's statement is the one measured, with its :drafts parameter.
            var read = await RelationAnchor.ReadAllAsync(session,
                [new PageLinkRelations().Statement, new BoardRelations().Statement, new ContactMentionRelations().Statement], space, null, true, default);
            ends.AddRange(read.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }));
            Assert.Equal(20_000, ends.Count / 2);
            statements.Add((RelationNodes.Sql, new Dictionary<string, object>
            {
                ["actor"] = f.Reader.Id, ["stamp"] = f.Reader.SecurityStamp, ["listing"] = true, ["space"] = space,
                ["ids"] = string.Join(',', ends.Distinct()), ["types"] = "atlas.page,atlas.board,atlas.contact", ["ai"] = false
            }));
        }
        string[] names = ["links", "placements", "mentions", "nodes"];
        for (var index = 0; index < statements.Count; index++)
        {
            var (sql, parameters) = statements[index];
            var plan = await ExplainAsync(f, sql, parameters);
            var root = plan["Plan"]!.AsObject();
            var buffers = root["Shared Hit Blocks"]!.GetValue<long>() + root["Shared Read Blocks"]!.GetValue<long>();
            output.WriteLine($"{names[index]}: database execution {plan["Execution Time"]} ms, {buffers} buffers");
            // The shape, not the clock (PERF3-4): one run measured 40 to 126 ms on a shared database VM, while a slow plan reads more pages
            // on any host.
            Assert.True(buffers <= Buffers[names[index]] * 3 / 2, $"{names[index]}: {buffers} buffers, today {Buffers[names[index]]}");
        }

        var relations = Stopwatch.StartNew();
        var around = await f.RelationsAsync(f.Reader, contents.Items[0].Id, 2);
        output.WriteLine($"relations at depth 2: {around.Nodes.Count} nodes, {around.Edges.Count} edges in {relations.Elapsed.TotalMilliseconds:0} ms");
    }

    /// <summary>The shared buffers of each statement over this fixture, measured on 2026-10-29.</summary>
    private static readonly Dictionary<string, long> Buffers = new()
    {
        ["links"] = 333, ["placements"] = 445, ["mentions"] = 183, ["nodes"] = 580,
    };

    internal static IEnumerable<JsonObject> Nodes(JsonObject node) =>
        new[] { node }.Concat((node["Plans"] as JsonArray ?? []).SelectMany(child => Nodes(child!.AsObject())));

    /// <summary>The statement's executed plan with its buffers (EXPLAIN ANALYZE, BUFFERS, FORMAT JSON), rolled back.</summary>
    internal static async Task<JsonObject> ExplainAsync(RelationsFixture f, string sql, IReadOnlyDictionary<string, object> parameters)
    {
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var query = session.CreateSQLQuery("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql).AddScalar("QUERY PLAN", NHibernateUtil.String);
        foreach (var (name, value) in parameters)
        {
            query.SetParameter(name, value);
        }
        var json = await query.UniqueResultAsync<string>();
        await transaction.RollbackAsync();
        return JsonNode.Parse(json)!.AsArray()[0]!.AsObject();
    }

    /// <summary>Plain SQL in one implicit transaction (casts and all, past NHibernate's parameter parser).</summary>
    internal static async Task SqlAsync(RelationsFixture f, string sql)
    {
        using var session = f.Host.OpenSession();
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }
}
