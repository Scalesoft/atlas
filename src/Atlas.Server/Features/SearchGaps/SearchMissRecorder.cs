using System.Collections.Concurrent;
using Atlas.Server.Features.SearchGaps.Commands;

namespace Atlas.Server.Features.SearchGaps;

/// <summary>One full omnisearch pass flagged recordMiss: who searched (in memory only), in which space (filter, else the
/// omnisearch context), the query as typed, its normalized form and whether it found any page or space.</summary>
public sealed record SearchObservation(Guid UserId, Guid? SpaceId, string? Query, string Normalized, bool Found,
    string Reason = Contracts.SearchGapReason.Zero);

/// <summary>Called by SearchHandler after the result is assembled. In memory and O(1): no I/O, no lock, never blocks.</summary>
public interface ISearchMissRecorder
{
    void Observe(SearchObservation observation);
}

/// <summary>
/// Merges the prefixes a user types into the query the user settled on. Each user has at most one pending miss. A newer query
/// of the same user and space that is a prefix of it, or that it is a prefix of, replaces it when it found nothing too, or
/// discards it when it found something. An unrelated query commits it, and so do 15 s of quiet (<see cref="Drain"/>).
/// SearchMissFlusher drains the committed misses into the database.
/// </summary>
/// <remarks>Lock-free: per-user state changes are compare-and-swap on immutable entries, so concurrent searches never wait on
/// each other. Pending plus committed entries are bounded by the capacity; a surplus is dropped with one warning until the
/// next drain makes room. Losing misses (overflow, crash) is acceptable: this is a statistic.</remarks>
public sealed class SearchMissRecorder : ISearchMissRecorder
{
    public const int DefaultCapacity = 10_000;
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, Pending> m_pending = new();
    private readonly ConcurrentQueue<SearchMiss> m_committed = new();
    private readonly TimeProvider m_clock;
    private readonly ILogger<SearchMissRecorder> m_logger;
    private readonly int m_capacity;
    private int m_count;
    private int m_warned;

    public SearchMissRecorder(TimeProvider clock, ILogger<SearchMissRecorder> logger, int capacity = DefaultCapacity)
    {
        m_clock = clock;
        m_logger = logger;
        m_capacity = capacity;
    }

    public void Observe(SearchObservation observation) => Observe(observation, m_clock.GetUtcNow());

    public void Observe(SearchObservation observation, DateTimeOffset now)
    {
        var next = !observation.Found && SearchMissPolicy.Allows(observation.Query, observation.Normalized)
            ? new Pending(new SearchMiss(observation.UserId, observation.SpaceId, observation.Normalized,
                SearchMissPolicy.Display(observation.Query), DateOnly.FromDateTime(now.UtcDateTime), observation.Reason), now)
            : null;
        while (true)
        {
            if (!m_pending.TryGetValue(observation.UserId, out var current))
            {
                if (next is null || !TryReserve())
                {
                    return;
                }
                if (m_pending.TryAdd(observation.UserId, next))
                {
                    return;
                }
                Release();
                continue;
            }
            var continued = now - current.SeenAt < Quiet && Related(current.Miss, observation);
            if (next is not null)
            {
                if (!m_pending.TryUpdate(observation.UserId, next, current))
                {
                    continue;
                }
                if (!continued && TryReserve())
                {
                    m_committed.Enqueue(current.Miss);
                }
                return;
            }
            if (!m_pending.TryRemove(KeyValuePair.Create(observation.UserId, current)))
            {
                continue;
            }
            if (continued)
            {
                Release();
            }
            else
            {
                // The pending entry's slot moves to the committed queue.
                m_committed.Enqueue(current.Miss);
            }
            return;
        }
    }

    /// <summary>Commits pending misses after 15 s of quiet (all of them when <paramref name="all"/>, at shutdown) and takes every
    /// committed miss.</summary>
    public IReadOnlyList<SearchMiss> Drain(DateTimeOffset now, bool all = false)
    {
        foreach (var entry in m_pending)
        {
            if ((all || now - entry.Value.SeenAt >= Quiet) && m_pending.TryRemove(entry))
            {
                m_committed.Enqueue(entry.Value.Miss);
            }
        }
        var misses = new List<SearchMiss>();
        while (m_committed.TryDequeue(out var miss))
        {
            misses.Add(miss);
            Release();
        }
        if (Volatile.Read(ref m_count) < m_capacity)
        {
            Interlocked.Exchange(ref m_warned, 0);
        }
        return misses;
    }

    private static bool Related(SearchMiss pending, SearchObservation observation)
        => pending.SpaceId == observation.SpaceId
           && (pending.Normalized.StartsWith(observation.Normalized, StringComparison.Ordinal)
               || observation.Normalized.StartsWith(pending.Normalized, StringComparison.Ordinal));

    private bool TryReserve()
    {
        if (Interlocked.Increment(ref m_count) <= m_capacity)
        {
            return true;
        }
        Interlocked.Decrement(ref m_count);
        if (Interlocked.Exchange(ref m_warned, 1) == 0)
        {
            m_logger.LogWarning("Search miss buffer is full ({Capacity} entries); further misses are dropped until the next flush", m_capacity);
        }
        return false;
    }

    private void Release() => Interlocked.Decrement(ref m_count);

    private sealed record Pending(SearchMiss Miss, DateTimeOffset SeenAt);
}
