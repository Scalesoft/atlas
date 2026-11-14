using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>
/// UX SE3-1 (migration 2026102873) and VYK1-2 (2026102890): the work search skips must never change its answer, and must stay skipped. A search took
/// 300–600 ms on the real wiki: per-call planning of atlas_search_lexemes for every heading of every hit, and the fuzzy body test of
/// every page even when the strict tests had filled the page.
/// </summary>
public sealed class SearchLatencyTests
{
    [PostgreSqlFact]
    public async Task A_page_found_only_by_its_misspelt_body_keeps_its_place_after_the_strict_hits()
    {
        await using var f = await SearchFixture.CreateAsync();
        foreach (var title in new[] { "Kalkulačka první", "Kalkulačka druhá", "Kalkulačka třetí" })
        {
            await Publish(f, await f.Create(title));
        }
        // "kalkulakca" is no word form of "kalkulačka" (full text misses it), but close enough for the fuzzy body test.
        var typo = await f.Pages.CreateAsync(new(f.SpaceId, null, "Účetnictví", SearchFixture.Rich("Stará kalkulakca v kanceláři"),
            "Stará kalkulakca v kanceláři"), f.Writer, default);
        await Publish(f, typo);

        // The strict hits fill the first page (and the one row past it): the body test cannot change it and is skipped.
        var first = await f.Find(f.Reader, "kalkulacka", limit: 2);
        Assert.Equal(["Kalkulačka druhá", "Kalkulačka první"], first.Items.Select(x => x.Title).Order());
        Assert.True(first.HasMore);
        // The next page needs it, and finds the page after the last strict hit.
        var second = await f.Find(f.Reader, "kalkulacka", offset: 2, limit: 2);
        Assert.Equal(["Kalkulačka třetí", "Účetnictví"], second.Items.Select(x => x.Title));
        Assert.False(second.HasMore);
        Assert.Equal("Účetnictví", (await f.Find(f.Reader, "kalkulacka")).Items[^1].Title);
        Assert.Equal("Účetnictví", (await f.Find(f.Reader, "kalkulacka", new SearchOptions("published"))).Items[^1].Title);
        // Ordered by date, the body hit competes with every strict hit, so the test always runs.
        Assert.Contains(typo.Id, (await f.Find(f.Reader, "kalkulacka", new SearchOptions(Sort: "published"))).Items.Select(x => x.Id));
    }

    [PostgreSqlFact]
    public async Task Headings_are_tested_one_by_one_only_on_a_page_whose_headings_match()
    {
        await using var f = await SearchFixture.CreateAsync();
        const string content = "{\"format\":\"atlas-rich\",\"version\":1,\"plugins\":[{\"id\":\"heading-anchors\",\"version\":1}],\"document\":{\"type\":\"doc\",\"content\":["
            + "{\"type\":\"heading\",\"attrs\":{\"level\":2,\"headingId\":\"uvod\"},\"content\":[{\"type\":\"text\",\"text\":\"Úvod\"}]},"
            + "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Kalkulačka\"}]}]}}";
        for (var i = 0; i < 3; i++)
        {
            await Publish(f, await f.Pages.CreateAsync(new(f.SpaceId, null, $"Kalkulačka {i}", content, "## Úvod\n\nKalkulačka"), f.Writer, default));
        }

        Assert.Equal(0, await LexemeCalls(f, "kalkulacka"));
        Assert.Equal(3, await LexemeCalls(f, "uvod"));
        Assert.Equal("Úvod", (await f.Find(f.Reader, "uvod")).Items[0].HeadingTitle);
    }

    /// <summary>VYK1-2 (migration 2026102890): the typo-tolerant body test reads the first 20 000 characters of a page, in the
    /// published search and the hybrid candidates alike.</summary>
    [PostgreSqlFact]
    public async Task The_fuzzy_body_test_reads_the_head_of_a_long_page()
    {
        await using var f = await SearchFixture.CreateAsync();
        var filler = string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet. ", 800));
        await Publish(f, await f.Pages.CreateAsync(new(f.SpaceId, null, "Pokladna", SearchFixture.Rich("Stará kalkulakca. " + filler), "Stará kalkulakca. " + filler), f.Writer, default));
        await Publish(f, await f.Pages.CreateAsync(new(f.SpaceId, null, "Archiv", SearchFixture.Rich(filler + " Stará kalkulakca."), filler + " Stará kalkulakca."), f.Writer, default));

        Assert.Equal(["Pokladna"], (await f.Find(f.Reader, "kalkulacka")).Items.Select(x => x.Title));
        Assert.Equal("Pokladna", await HybridAsync(f, 20, false));
    }

    /// <summary>VYK1-2: the hybrid candidates skip the fuzzy body test when the strict hits fill their window, as the published
    /// search does; ordered by date it still runs. Counted as calls of the trigram operator's function.</summary>
    [PostgreSqlFact]
    public async Task The_hybrid_candidates_skip_the_body_test_behind_a_full_window_of_strict_hits()
    {
        // Own database: the budget counts calls of the trigram operator over every published row the query's plan reaches before the
        // access filter, so pages of other tests would be counted too.
        await using var f = await SearchFixture.CreateAsync(isolated: true);
        const int bodyOnly = 12;
        for (var i = 0; i < 3; i++)
        {
            await Publish(f, await f.Create($"Kalkulačka {i}"));
        }
        for (var i = 0; i < bodyOnly; i++)
        {
            await Publish(f, await f.Pages.CreateAsync(new(f.SpaceId, null, $"Účetnictví {i}", SearchFixture.Rich("Stará kalkulakca v kanceláři"),
                "Stará kalkulakca v kanceláři"), f.Writer, default));
        }

        var (_, guarded) = await TrigramCallsAsync(f, latest: false);
        var (_, forced) = await TrigramCallsAsync(f, latest: true);
        // Behind the full window only the title tests run (once per page, once more for the strict count at most), never the body's.
        Assert.True(guarded <= 2 * bodyOnly && forced >= guarded + bodyOnly, $"{guarded} trigram tests behind a full window, {forced} ordered by date");
    }

    private static async Task<(string? Titles, long Calls)> TrigramCallsAsync(SearchFixture f, bool latest)
    {
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        await new SearchSql(session).PrepareAsync(default);
        await session.CreateSQLQuery("SELECT set_config('track_functions','all',true)").UniqueResultAsync();
        var titles = await HybridAsync(session, f.Reader.Id, 2, latest);
        return (titles, await session.CreateSQLQuery("SELECT COALESCE(pg_stat_get_xact_function_calls(CAST('word_similarity_op(text,text)' AS regprocedure)),0)")
            .UniqueResultAsync<long>());
    }

    private static async Task<string?> HybridAsync(SearchFixture f, int take, bool latest)
    {
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        await new SearchSql(session).PrepareAsync(default);
        return await HybridAsync(session, f.Reader.Id, take, latest);
    }

    /// <summary>The lexical part of the hybrid candidates (no index generation) for "kalkulacka", titles in rank order.</summary>
    private static async Task<string?> HybridAsync(NHibernate.ISession session, Guid actor, int take, bool latest)
    {
        var stamp = await session.CreateSQLQuery("SELECT \"SecurityStamp\" FROM \"UserAccount\" WHERE \"Id\"=:id").SetParameter("id", actor).UniqueResultAsync<string>();
        return await session.CreateSQLQuery("SELECT string_agg(title, ',' ORDER BY lexical_rank) FROM (" + SearchQueries.HybridCandidates() + ") moved")
            .SetParameter("query", "kalkulacka").SetParameter("prefix", "kalkulacka%").SetParameter("fts", "(kalkulacka:* | kalkulack)")
            .SetParameter("phrase", false).SetParameter("fuzzy", true).SetParameter("actor", actor).SetParameter("stamp", stamp)
            .SetParameter("space", null, NHibernate.NHibernateUtil.Guid).SetParameter("lexical_take", take)
            .SetParameter("changed_since", null, NHibernate.NHibernateUtil.UtcDateTime).SetParameter("latest", latest)
            .SetParameter("generation", null, NHibernate.NHibernateUtil.Guid).SetParameter("embedding", null, NHibernate.NHibernateUtil.StringClob)
            .SetParameter("min_similarity", 0.5f, NHibernate.NHibernateUtil.Single)
            .UniqueResultAsync<string?>();
    }

    /// <summary>The lexemes of headings one published search computes (SearchSqlText.Lexemes, atlas_search_lexemes until Atlas 3.6),
    /// counted as calls of unaccent, which each computation makes once and nothing else of that search makes.</summary>
    private static async Task<long> LexemeCalls(SearchFixture f, string query)
    {
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var sql = new SearchSql(session);
        await sql.PrepareAsync(default);
        await session.CreateSQLQuery("SELECT set_config('track_functions','all',true)").UniqueResultAsync();
        var stamp = await session.CreateSQLQuery("SELECT \"SecurityStamp\" FROM \"UserAccount\" WHERE \"Id\"=:id")
            .SetParameter("id", f.Reader.Id).UniqueResultAsync<string>();
        var parameters = SearchQueryParameters.Create(query, SearchText.Normalize(query), f.Reader.Id, stamp, null);
        Assert.NotEmpty(await sql.PublishedAsync(parameters, 21, 0, default));
        return await session.CreateSQLQuery("SELECT COALESCE(pg_stat_get_xact_function_calls(CAST('public.unaccent(text)' AS regprocedure)),0)")
            .UniqueResultAsync<long>();
    }

    private static async Task Publish(SearchFixture f, PageDto page) => await f.Pages.PublishAsync(page.Id, new(page.Version), f.Writer, default);
}
