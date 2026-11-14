using System.Diagnostics;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Resources;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Relations.UsageTests;

namespace Atlas.Server.Tests.Relations;

/// <summary>
/// The time budget of spec §7 (p95 150 ms) for „Použito na“ (plan 3.1 M5) of the most used contact of a large space: 2 000 published
/// pages that mention it and 1 000 boards that show it, read by a Reader (EffectivePageAccess, not the WikiAdmin shortcut). The use
/// list (a page of 50 with the reasons of its places) and the badge count are bounded by the clock as well as by the mention
/// statement's buffers with drafts; the space's inventory reads the whole space as Obsah does and is written out (ContentsBudgetTests
/// bounds that read's shape).
/// </summary>
[Collection(RelationsPerformanceCollection.Name)]
public sealed class UsageBudgetTests(ITestOutputHelper output)
{
    private const int Pages = 2000;
    private const int Boards = 1000;
    private const int Runs = 20;
    private const double BudgetMs = 150;

    [PostgreSqlFact]
    public async Task The_uses_of_the_most_used_contact_list_and_count_within_the_budget()
    {
        // Own host: the bounds are the wall-clock p95 of the use list and count and the mention statement's buffers, calibrated on
        // tables of only this test's rows; on the shared host the list's p95 was 269 ms against 150 ms.
        await using var f = await RelationsFixture.CreateAsync();
        var (space, owner) = (f.SpaceId, f.Writer.Id);
        var hub = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana");
        await ContentsBudgetTests.SqlAsync(f, $$"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT md5('page' || n)::uuid, '{{space}}', '{{owner}}', 1 FROM generate_series(1, {{Pages}}) n;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT md5('revision' || n)::uuid, md5('page' || n)::uuid, 1, 'Stránka ' || n, '{{Doc(Paragraph("Za provoz odpovídá", Mention(hub)))}}', 'Text',
                '{{owner}}', now(), 'web'
              FROM generate_series(1, {{Pages}}) n;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."SpaceId"='{{space}}';
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder")
              SELECT md5('board' || n)::uuid, '{{space}}', 'Tabule ' || n, 1, now(), '{{owner}}', now(), false, 1024 * n FROM generate_series(1, {{Boards}}) n;
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","TargetResourceId","Version","IsDeleted","PropsJson")
              SELECT md5('card' || b)::uuid, md5('board' || b)::uuid, 'object', 0, 0, 240, 160, 1, '{{hub}}', 1, false, '{"view":"card"}'::jsonb
              FROM generate_series(1, {{Boards}}) b;
            {{Atlas.Server.Tests.Platform.DerivedRows.Sql}}
            """);
        await f.DeriveAsync();
        await ContentsBudgetTests.SqlAsync(f, "ANALYZE;");

        var list = await MeasureAsync("usage list", () => f.As<ObjectUsageList>(f.Reader, new GetObjectUsage(hub)));
        Assert.Equal((Pages + Boards, 0, GetObjectUsage.PageSize), (list.Result.Total, list.Result.Hidden, list.Result.Items.Count));
        Assert.All(list.Result.Items.Where(row => row.Kind == RelationKinds.Mention), row => Assert.Equal("Za provoz odpovídá Jana", row.Excerpt));
        var count = await MeasureAsync("usage count", () => f.As<UsageCount>(f.Reader, new CountObjectUsage(hub)));
        Assert.Equal(new UsageCount(hub, Pages + Boards, Pages + Boards, 0), count.Result);
        var inventory = await MeasureAsync("space inventory", () => f.As<SpaceUsage>(f.Reader, new ListSpaceUsage(space)));
        Assert.Equal(Pages + Boards + 1, inventory.Result.Types.Values.Sum());

        // The clock, on the reads of one object only; the whole space's read is bounded by its shape.
        Assert.True(list.P95 <= BudgetMs, $"usage list p95 {list.P95:0} ms > {BudgetMs} ms");
        Assert.True(count.P95 <= BudgetMs, $"usage count p95 {count.P95:0} ms > {BudgetMs} ms");

        // The mention statement with drafts around one object: the projection's object index, not the table.
        var plan = await ContentsBudgetTests.ExplainAsync(f, RelationAnchor.Cte + ContactMentionRelations.Sql,
            new Dictionary<string, object> { ["whole"] = false, ["space"] = Guid.Empty, ["around"] = hub.ToString(), ["drafts"] = true });
        var root = plan["Plan"]!.AsObject();
        var buffers = root["Shared Hit Blocks"]!.GetValue<long>() + root["Shared Read Blocks"]!.GetValue<long>();
        output.WriteLine($"mentions of one contact with drafts: database execution {plan["Execution Time"]} ms, {buffers} buffers");
        // 113 buffers measured on 2026-10-29 (p95: list 73 ms, count 72 ms, inventory 80 ms); half again as ContentsBudgetTests allows.
        Assert.True(buffers <= 113 * 3 / 2, $"mentions with drafts: {buffers} buffers, today 113");
    }

    private async Task<(T Result, double P95)> MeasureAsync<T>(string name, Func<Task<T>> read)
    {
        var result = default(T)!;
        var times = new List<double>();
        for (var run = 0; run < Runs + 3; run++)
        {
            var watch = Stopwatch.StartNew();
            result = await read();
            watch.Stop();
            if (run >= 3)
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        times.Sort();
        var p95 = times[(int)Math.Ceiling(Runs * 0.95) - 1];
        output.WriteLine($"{name}: median {times[Runs / 2]:0} ms, p95 {p95:0} ms, max {times[^1]:0} ms");
        return (result, p95);
    }
}
