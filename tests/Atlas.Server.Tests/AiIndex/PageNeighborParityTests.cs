using Xunit;

namespace Atlas.Server.Tests.AiIndex;

/// <summary>
/// No logic in the database (3.6): the neighbour lists were written by atlas_refresh_page_neighbors and
/// atlas_refresh_generation_neighbors; C# writes them now. The expected lists were captured from those functions over the same pages,
/// so the C# statements must reproduce rank, order, trimming to 30 and the similarity of both the incremental refresh and the full recompute.
/// </summary>
public sealed class PageNeighborParityTests
{
    private const int Pages = 34;

    private static readonly string[] Words =
    [
        "kotva", "lodni", "plachta", "vitr", "pristav", "kapitan", "zaliv", "vlna", "stozar", "kormidlo", "paluba", "lano",
        "maják", "bóje", "příliv", "odliv", "útes", "záliv", "rybář", "síť", "motor", "vrtule", "kýl", "podpalubí",
    ];

    [PostgreSqlFact]
    public async Task Incremental_and_full_refresh_keep_the_lists_the_functions_wrote()
    {
        await using var f = await IndexFixture.CreateAsync();
        var random = new Random(20261104);
        for (var i = 0; i < Pages; i++)
        {
            // A seeded draw of words from a small pool: pages overlap by chance and no two pages are alike, so similarities do not tie.
            await f.PublishNewAsync($"P{i:00}", string.Join(" ", Enumerable.Range(0, 10).Select(_ => Words[random.Next(Words.Length)])) + $" zz{random.Next(1000, 9999)}");
        }
        await f.ActiveIndexAsync();
        var incremental = await ListsAsync(f);

        await f.Maintenance.RefreshNeighborsAsync(f.Clock.GetUtcNow().AddHours(25), default);
        var full = await ListsAsync(f);

        Assert.True(Expected.Incremental == incremental, $"incremental: {incremental}");
        Assert.True(Expected.Full == full, $"full: {full}");
    }

    /// <summary>One line per page: its neighbours in rank order with the rank and similarity (a neighbour tied on similarity within the list shows as *, since the page id breaks such a tie); reduced to a count and a fingerprint.</summary>
    private static async Task<string> ListsAsync(IndexFixture f)
    {
        using var session = f.Host.OpenSession();
        var text = Convert.ToString(await session.CreateSQLQuery("""
            SELECT string_agg(l, E'\n' ORDER BY l) FROM (
              SELECT t || '=' || string_agg(CASE WHEN ties = 1 THEN nb ELSE '*' END || ':' || rank || ':' || sim, ',' ORDER BY rank) AS l FROM (
                SELECT a."Title" AS t, b."Title" AS nb, n."Rank" AS rank, round(CAST(n."Similarity" AS numeric), 3) AS sim,
                  count(*) OVER (PARTITION BY n."PageId", round(CAST(n."Similarity" AS numeric), 3)) AS ties
                FROM "PageNeighbor" n
                JOIN "Page" pa ON pa."Id" = n."PageId" JOIN "PageRevision" a ON a."Id" = pa."PublishedRevisionId"
                JOIN "Page" pb ON pb."Id" = n."NeighborPageId" JOIN "PageRevision" b ON b."Id" = pb."PublishedRevisionId") y
              GROUP BY t) x
            """).UniqueResultAsync())!;
        return $"{text.Split('\n').Length} pages, md5 {Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text)))}";
    }

    private static class Expected
    {
        public const string Incremental = "34 pages, md5 61d583a8692958c9ade491f2190725fb";
        public const string Full = "34 pages, md5 61d583a8692958c9ade491f2190725fb";
    }
}
