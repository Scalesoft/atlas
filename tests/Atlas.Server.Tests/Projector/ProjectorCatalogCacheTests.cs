using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Persistence;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>Review A2-2/S2-5: the catalog cache holds one entry per catalog page, never the page's content, within a byte budget.</summary>
public sealed class ProjectorCatalogCacheTests
{
    private static readonly Guid Page = Guid.NewGuid();

    [Fact]
    public void A_new_revision_of_a_page_replaces_its_entry_and_a_changed_content_hash_misses()
    {
        var cache = new ProjectorCatalogCache(budget: 1_000_000);
        var first = Guid.NewGuid();
        cache.Add(Page, first, "h1", 1_000, Definitions(1));
        for (var i = 0; i < 100; i++)
        {
            cache.Add(Page, Guid.NewGuid(), $"r{i}", 1_000, Definitions(1));
        }
        var last = Guid.NewGuid();
        cache.Add(Page, last, "h2", 1_000, Definitions(2));

        Assert.Equal(1, cache.Count);
        Assert.Equal(2, cache.Find(Page, last, "h2")!.Count);
        Assert.Null(cache.Find(Page, first, "h1"));
        // An import or a source replace changes a revision in place: the same revision with other content is a miss.
        Assert.Null(cache.Find(Page, last, "h3"));
    }

    [Fact]
    public void The_cache_stays_within_its_byte_budget_and_does_not_keep_a_page_larger_than_it()
    {
        var cache = new ProjectorCatalogCache(budget: 10_000);
        for (var i = 0; i < 50; i++)
        {
            cache.Add(Guid.NewGuid(), Guid.NewGuid(), "h", 1_000, Definitions(1));
        }
        var huge = Guid.NewGuid();
        var revision = Guid.NewGuid();
        cache.Add(huge, revision, "h", 1_000_000, Definitions(1));

        Assert.InRange(cache.Bytes, 1, 10_000);
        Assert.Null(cache.Find(huge, revision, "h"));
    }

    private static IReadOnlyList<CatalogDefinition> Definitions(int count) => Enumerable.Range(0, count)
        .Select(i => new CatalogDefinition($"k{i}", "T", "T", "internal", [], new JsonObject { ["type"] = "requirement" }, Page, "", "")).ToArray();
}
