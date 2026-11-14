using System.Diagnostics;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Entities.Queries;
using Atlas.Server.Tests.Relations;
using NHibernate;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.Entities;

/// <summary>
/// The time budget of spec §7 (p95 150 ms) for the contact overview and its export over ~37 000 occurrences (PERF1-1, PER1-4), read
/// by a Reader (the page rights of EffectivePageAccess). Bounded is what makes the time on any host (PERF2-2): the executed plan of the
/// handler's one statement reads the occurrences once and touches about as many pages as today. The milliseconds, of the database and
/// of the handler, are written out, not bounded; the statement count is PerformanceBudgetHttpTests'.
/// </summary>
[Collection(RelationsPerformanceCollection.Name)]
public sealed class EntitiesBudgetTests(ITestOutputHelper output)
{
    private const int Pages = 3000;
    private const int PerPage = 6;
    private const int People = 5000;
    private const int Directory = 1000;
    private const int Runs = 20;

    [PostgreSqlFact]
    public async Task Thirty_seven_thousand_occurrences_list_and_export_within_the_budget()
    {
        // Own host: the instance overview reads every occurrence of the wiki (its total, rows read and buffers are of the whole
        // EntityOccurrence table, which the seed empties first), so another test's rows would change what is bounded.
        await using var f = await RelationsFixture.CreateAsync();
        var (space, owner) = (f.SpaceId, f.Writer.Id);
        // 3 000 published pages of 6 cards each in both revisions (36 000 occurrences) over 5 000 people, and 1 000 of them in the wiki
        // directory: the catalog rows are written directly, as the consumer would.
        await ContentsBudgetTests.SqlAsync(f, $$"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT md5('page' || n)::uuid, '{{space}}', '{{owner}}', 1 FROM generate_series(1, {{Pages}}) n;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT md5('revision' || n)::uuid, md5('page' || n)::uuid, 1, 'Stránka ' || n, '{}', 'Text', '{{owner}}', now(), 'web'
              FROM generate_series(1, {{Pages}}) n;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."SpaceId"='{{space}}';
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","CreatedAt","Title","Data")
              SELECT md5('contact' || n)::uuid, 'atlas.contact', 'wiki', NULL, now(), 'Osoba ' || n,
                jsonb_build_object('name', 'Osoba ' || n, 'emails', jsonb_build_array('osoba' || n || '@example.com'))
              FROM generate_series(1, {{Directory}}) n;
            {{Atlas.Server.Tests.Platform.DerivedRows.Sql}}
            """);
        await ContentsBudgetTests.SqlAsync(f, $$"""
            DELETE FROM "EntityOccurrence";
            INSERT INTO "EntityOccurrence"("ResourceId","RevisionKind","Kind","IdentityKey","Attributes","Anchor")
              SELECT md5('page' || p)::uuid, kind, 'contact', 'email:osoba' || (1 + (p * {{PerPage}} + k) % {{People}}) || '@example.com',
                jsonb_build_object('name', 'Osoba ' || (1 + (p * {{PerPage}} + k) % {{People}}), 'organization', 'Firma ' || k,
                  'emails', jsonb_build_array('osoba' || (1 + (p * {{PerPage}} + k) % {{People}}) || '@example.com')), 'tym'
              FROM generate_series(1, {{Pages}}) p CROSS JOIN generate_series(1, {{PerPage}}) k CROSS JOIN (VALUES ('working'), ('published')) v(kind);
            INSERT INTO "EntityOccurrence"("ResourceId","RevisionKind","Kind","IdentityKey","Attributes")
              SELECT md5('contact' || n)::uuid, 'published', 'contact', 'email:osoba' || n || '@example.com', "Data"
              FROM generate_series(1, {{Directory}}) n JOIN "ContentResource" r ON r."Id"=md5('contact' || n)::uuid;
            ANALYZE;
            """);

        var queries = new (string Name, ListEntities Query)[]
        {
            ("list", new ListEntities(EntityKinds.Contact, "instance", null, null)),
            ("page 40", new ListEntities(EntityKinds.Contact, "instance", null, null, 40)),
            ("filter", new ListEntities(EntityKinds.Contact, "instance", null, "osoba 12")),
            ("space", new ListEntities(EntityKinds.Contact, "space", space, null)),
            ("export", new ListEntities(EntityKinds.Contact, "instance", null, null, All: true)),
        };
        var list = await f.As<EntityList>(f.Reader, queries[0].Query);
        Assert.Equal(People, list.Total);
        Assert.Equal(ListEntities.PageSize, list.Items.Count);
        Assert.Equal(People, (await f.As<EntityList>(f.Reader, queries[^1].Query)).Items.Count);

        foreach (var (name, query) in queries)
        {
            var times = new List<double>();
            for (var run = 0; run < Runs + 3; run++)
            {
                var watch = Stopwatch.StartNew();
                await f.As<EntityList>(f.Reader, query);
                if (run >= 3)
                {
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }
            }
            times.Sort();
            var parameters = ListEntitiesHandler.Parameters(new Dictionary<string, object>
            {
                ["actor"] = f.Reader.Id, ["stamp"] = f.Reader.SecurityStamp, ["listing"] = false, ["space"] = Guid.Empty,
                ["types"] = "atlas.page,atlas.contact", ["ai"] = false
            }, query);
            var plan = await ContentsBudgetTests.ExplainAsync(f, ListEntitiesHandler.Sql, parameters);
            var root = plan["Plan"]!.AsObject();
            var nodes = ContentsBudgetTests.Nodes(root).ToArray();
            var scans = nodes.Where(node => node["Relation Name"]?.GetValue<string>() == "EntityOccurrence").ToArray();
            var processes = 1 + nodes.Select(node => node["Workers Launched"]?.GetValue<int>() ?? 0).DefaultIfEmpty().Max();
            var rows = scans.Sum(node => node["Actual Rows"]!.GetValue<double>() * node["Actual Loops"]!.GetValue<double>());
            var buffers = root["Shared Hit Blocks"]!.GetValue<long>() + root["Shared Read Blocks"]!.GetValue<long>();
            output.WriteLine($"{name}: database execution {plan["Execution Time"]} ms, {buffers} buffers, {rows} occurrence rows in {scans.Length} scans; " +
                $"handler wall clock median {times[Runs / 2]:0} ms, p95 {times[(int)Math.Ceiling(Runs * 0.95) - 1]:0} ms");
            // The shape, not the clock (PERF2-2): the occurrences are read once, never per entity or page, and the pages touched stay
            // near today's; both hold on any host, while a slower runner only adds milliseconds.
            Assert.True(scans.All(node => node["Actual Loops"]!.GetValue<int>() <= processes), $"{name}: occurrences scanned in a loop");
            // Two passes (the scope's resources, then their occurrences) read 2 × 37 000 rows today, exactly the bound: a parallel scan
            // reports each loop's rows as a rounded average, so each loop may add a row (PERF3-4).
            var loops = scans.Sum(node => node["Actual Loops"]!.GetValue<double>());
            Assert.True(rows <= 2 * Occurrences + loops, $"{name}: {rows} occurrence rows read for {Occurrences} occurrences");
            Assert.True(buffers <= Buffers[name] * 3 / 2, $"{name}: {buffers} buffers, today {Buffers[name]}");
        }
    }

    private const int Occurrences = Pages * PerPage * 2 + Directory;

    /// <summary>The shared buffers of each query over this fixture, measured on 2026-10-29.</summary>
    private static readonly Dictionary<string, long> Buffers = new()
    {
        ["list"] = 2217, ["page 40"] = 2217, ["filter"] = 2217, ["space"] = 2289, ["export"] = 2217,
    };

}
