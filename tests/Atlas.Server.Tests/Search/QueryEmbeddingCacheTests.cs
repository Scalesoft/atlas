using Microsoft.Extensions.Time.Testing;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>The in-memory query vectors (R4): 10 minutes, 2 000 entries, one model call for a query in flight, and the compact
/// text the search sends them as.</summary>
public sealed class QueryEmbeddingCacheTests
{
    [Fact]
    public async Task Vectors_expire_after_ten_minutes_and_the_least_recently_used_leave_first()
    {
        var clock = new FakeTimeProvider();
        var cache = new QueryEmbeddingCache(clock);
        var generation = Guid.NewGuid();
        var first = new QueryEmbeddingKey(generation, "prvni");
        await cache.GetOrStart(first, () => Task.FromResult<float[]?>([1f]));
        Assert.True(cache.TryGet(first, out var vector));
        Assert.Equal([1f], vector);
        // Another generation never meets the vector.
        Assert.False(cache.TryGet(first with { GenerationId = Guid.NewGuid() }, out _));
        clock.Advance(QueryEmbeddingCache.Lifetime);
        Assert.False(cache.TryGet(first, out _));

        for (var i = 0; i <= QueryEmbeddingCache.Capacity; i++)
        {
            await cache.GetOrStart(new QueryEmbeddingKey(generation, "dotaz " + i), () => Task.FromResult<float[]?>([i]));
            if (i == 0)
            {
                continue;
            }
            // Keep the first query in use: the second one is the least recently used when the capacity is exceeded.
            Assert.True(cache.TryGet(new QueryEmbeddingKey(generation, "dotaz 0"), out _));
        }
        Assert.Equal(QueryEmbeddingCache.Capacity, cache.Count);
        Assert.True(cache.TryGet(new QueryEmbeddingKey(generation, "dotaz 0"), out _));
        Assert.False(cache.TryGet(new QueryEmbeddingKey(generation, "dotaz 1"), out _));
    }

    [Fact]
    public async Task A_query_in_flight_is_embedded_once_and_a_failure_is_not_kept()
    {
        var cache = new QueryEmbeddingCache(new FakeTimeProvider());
        var key = new QueryEmbeddingKey(Guid.NewGuid(), "vpn");
        var answer = new TaskCompletionSource<float[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var a = cache.GetOrStart(key, () => { starts++; return answer.Task; });
        var b = cache.GetOrStart(key, () => { starts++; return answer.Task; });
        Assert.Same(a, b);
        answer.SetResult(null);
        Assert.Null(await a);
        Assert.False(cache.TryGet(key, out _));
        var stored = await cache.GetOrStart(key, () => { starts++; return Task.FromResult<float[]?>([2f]); });
        Assert.Equal(new[] { 2f }, stored);
        Assert.Equal(2, starts);
        Assert.True(cache.TryGet(key, out _));
    }

    [Fact]
    public void The_compact_vector_text_keeps_the_direction_and_fits_one_write()
    {
        var vector = FakeAi.Vector("Připojení k firemní síti z domova přes VPN klienta a ověření telefonem");
        var text = QueryVectorText.Format(vector);
        Assert.True(text.Length < 6000, text.Length.ToString());
        var parsed = text.Trim('[', ']').Split(',').Select(float.Parse).ToArray();
        Assert.True(FakeAi.Cosine(vector, parsed) > 0.999999);
    }
}
