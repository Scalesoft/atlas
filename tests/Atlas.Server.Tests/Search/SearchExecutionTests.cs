using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.SearchGaps;
using Xunit;
using SearchMessage = Atlas.Server.Features.Search.Queries.Search;

namespace Atlas.Server.Tests.Search;

public sealed class SearchExecutionTests
{
    [PostgreSqlFact]
    public async Task Search_disables_jit_only_for_its_transaction()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        using var session = fixture.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.CreateSQLQuery("SELECT set_config('jit','on',false), set_config('jit_above_cost','0',true)")
            .UniqueResultAsync();
        var baseline = string.Join('\n', await session.CreateSQLQuery("EXPLAIN SELECT sum(i) FROM generate_series(1,100) i")
            .ListAsync<string>());
        Assert.Contains("JIT:", baseline, StringComparison.Ordinal);

        await new SearchSql(session).PrepareAsync(default);

        var searchPlan = string.Join('\n', await session.CreateSQLQuery("EXPLAIN SELECT sum(i) FROM generate_series(1,100) i")
            .ListAsync<string>());
        Assert.DoesNotContain("JIT:", searchPlan, StringComparison.Ordinal);
        await transaction.CommitAsync();
        Assert.Equal("on", await session.CreateSQLQuery("SELECT current_setting('jit')").UniqueResultAsync<string>());
    }

    // G2: only the omnisearch full pass flags recordMiss; any other caller or shape of search reaches the recorder never.
    [PostgreSqlFact]
    public async Task Only_flagged_zero_result_searches_reach_the_miss_recorder()
    {
        var recorder = new FakeRecorder();
        await using var f = await SearchFixture.CreateAsync(configure: services => services.AddSingleton<ISearchMissRecorder>(recorder));
        var page = await f.Create("Připojení k VPN");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        var context = Guid.NewGuid();

        Task<SearchResponse> Find(string query, Guid? spaceId = null, int offset = 0, SearchOptions? options = null, bool flag = true)
        {
            f.Host.Actor.Current = f.Reader;
            return f.Host.InvokeAsync<SearchResponse>(new SearchMessage(query, spaceId, offset, 20, options ?? new SearchOptions())
                { RecordMiss = flag, ContextSpaceId = context });
        }

        await Find("návod na tiskárnu", flag: false);
        await Find("návod na tiskárnu", offset: 20);
        await Find("návod na tiskárnu", options: new SearchOptions("drafts"));
        await Find("návod na tiskárnu", options: new SearchOptions(ChangedWithin: "week"));
        Assert.Empty(recorder.Observations);

        await Find("Návod na tiskárnu");
        await Find("vpn", spaceId: f.SpaceId, options: new SearchOptions("published"));
        Assert.Equal([(f.Reader.Id, (Guid?)context, "Návod na tiskárnu", "navod na tiskarnu", false), (f.Reader.Id, f.SpaceId, "vpn", "vpn", true)],
            recorder.Observations.Select(o => (o.UserId, o.SpaceId, o.Query, o.Normalized, o.Found)));
    }

    /// <summary>Review S2-3, P2-7: a page found only by the fallback over the significant words is a miss of the whole query (the
    /// miss log and "did you mean" count the strict result), the response says it relaxed, and a next page that says so pages the
    /// relaxed result without the strict probe; a caller that says it did not relax gets no fallback.</summary>
    [PostgreSqlFact]
    public async Task A_relaxed_result_is_flagged_and_still_a_miss_of_the_whole_query()
    {
        var recorder = new FakeRecorder();
        await using var f = await SearchFixture.CreateAsync(configure: services => services.AddSingleton<ISearchMissRecorder>(recorder));
        foreach (var title in new[] { "Připojení k VPN", "VPN z domova", "Domácí síť" })
        {
            var page = await f.Create(title);
            await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        }
        Task<SearchResponse> Find(string query, int offset = 0, bool? relaxed = null, int limit = 20)
        {
            f.Host.Actor.Current = f.Reader;
            return f.Host.InvokeAsync<SearchResponse>(new SearchMessage(query, null, offset, limit, new SearchOptions()) { RecordMiss = true, Relaxed = relaxed });
        }

        var question = await Find("Jak se připojím k VPN z domova?");
        var strict = await Find("VPN domova");

        Assert.True(question.Relaxed);
        Assert.NotEmpty(question.Items);
        Assert.False(strict.Relaxed);
        Assert.Equal([("Jak se připojím k VPN z domova?", false), ("VPN domova", true)], recorder.Observations.Select(o => (o.Query, o.Found)));
        // The next page of the relaxed result, told it is one, continues it; told it is not, it stays strict and empty.
        var first = await Find("Jak se připojím k VPN z domova?", limit: 1);
        var second = await Find("Jak se připojím k VPN z domova?", offset: 1, relaxed: true, limit: 1);
        Assert.True(second.Relaxed);
        Assert.NotEqual(Assert.Single(first.Items).Id, Assert.Single(second.Items).Id);
        Assert.Empty((await Find("Jak se připojím k VPN z domova?", relaxed: false)).Items);
    }

    /// <summary>P2-7: the fallback runs under its own short statement timeout, and one it exceeds is an empty result that leaves the
    /// search transaction usable, with the search's own timeout back.</summary>
    [PostgreSqlFact]
    public async Task A_statement_past_its_bound_is_empty_and_the_transaction_goes_on()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        using var session = fixture.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var sql = new SearchSql(session);
        await sql.PrepareAsync(default);

        var slow = await sql.BoundedAsync<IList<object[]>>(() => session.CreateSQLQuery("SELECT pg_sleep(2), 1").ListAsync<object[]>(), 50, [], default);
        var fast = await sql.BoundedAsync<IList<object[]>>(() => session.CreateSQLQuery("SELECT 1, 2").ListAsync<object[]>(), 50, [], default);

        Assert.Empty(slow);
        Assert.Single(fast);
        Assert.Equal("2s", await session.CreateSQLQuery("SELECT current_setting('statement_timeout')").UniqueResultAsync<string>());
        await transaction.CommitAsync();
    }

    private sealed class FakeRecorder : ISearchMissRecorder
    {
        public ConcurrentQueue<SearchObservation> Observations { get; } = new();
        public void Observe(SearchObservation observation) => Observations.Enqueue(observation);
    }
}
