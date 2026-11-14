using Microsoft.Extensions.Logging;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.SearchGaps;

/// <summary>Omnisearch searches while the user types, so the in-memory recorder merges the prefixes of one user's query and
/// keeps only the query the user settled on. Time is a parameter; nothing here touches a database.</summary>
public sealed class SearchMissRecorderTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private readonly CaptureLoggerProvider m_logs = new();

    [Fact]
    public void Typing_a_longer_zero_result_query_replaces_its_pending_prefix()
    {
        var recorder = Recorder();
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();

        recorder.Observe(Miss(user, space, "vpn kl"), s_start);
        recorder.Observe(Miss(user, space, "VPN klient Mac"), s_start.AddSeconds(1));

        Assert.Empty(recorder.Drain(s_start.AddSeconds(15)));
        var miss = Assert.Single(recorder.Drain(s_start.AddSeconds(16)));
        Assert.Equal((user, (Guid?)space, "vpn klient mac", "vpn klient mac", new DateOnly(2026, 10, 4)),
            (miss.UserId, miss.SpaceId, miss.Normalized, miss.Display, miss.On));
        Assert.Empty(recorder.Drain(s_start.AddMinutes(1)));
    }

    [Fact]
    public void A_related_query_with_results_discards_the_pending_miss_and_an_unrelated_one_commits_it()
    {
        var recorder = Recorder(capacity: 3);
        var user = Guid.NewGuid();

        recorder.Observe(Miss(user, null, "vpn klx"), s_start);
        recorder.Observe(Miss(user, null, "vpn kl", found: true), s_start.AddSeconds(1));
        Assert.Empty(recorder.Drain(s_start.AddHours(1), all: true));

        recorder.Observe(Miss(user, null, "tiskárna"), s_start);
        recorder.Observe(Miss(user, null, "dovolená"), s_start.AddSeconds(2));
        Assert.Equal(["tiskarna"], recorder.Drain(s_start.AddSeconds(3)).Select(miss => miss.Normalized));
        Assert.Equal(["dovolena"], recorder.Drain(s_start.AddSeconds(3), all: true).Select(miss => miss.Normalized));

        // Memory is bounded: past the capacity the surplus is dropped with one warning, never blocking the search.
        foreach (var index in Enumerable.Range(0, 5))
        {
            recorder.Observe(Miss(Guid.NewGuid(), null, "dotaz " + index), s_start);
        }
        Assert.Equal(3, recorder.Drain(s_start.AddMinutes(1)).Count);
        Assert.Single(m_logs.Entries, entry => entry.Level == LogLevel.Warning);
        recorder.Observe(Miss(user, null, "znovu volno"), s_start.AddMinutes(2));
        Assert.Single(recorder.Drain(s_start.AddMinutes(3)));
    }

    private SearchMissRecorder Recorder(int capacity = SearchMissRecorder.DefaultCapacity)
        => new(TimeProvider.System, LoggerFactory.Create(logging => logging.AddProvider(m_logs)).CreateLogger<SearchMissRecorder>(), capacity);

    private static SearchObservation Miss(Guid user, Guid? space, string query, bool found = false)
        => new(user, space, query, SearchText.Normalize(query), found);
}
