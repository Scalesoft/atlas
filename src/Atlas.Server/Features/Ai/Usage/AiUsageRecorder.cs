using System.Collections.Concurrent;

namespace Atlas.Server.Features.Ai.Usage;

/// <summary>
/// One model call as an <c>AiUsage</c> row (spec RAG §2, §7): task, profile kind and revision, model, item count, token
/// counts, latency and outcome code. Never the prompt, answer, query, chunk, upstream error or user. Null tokens mean
/// unknown, not zero.
/// </summary>
/// <param name="OccurredAt">UTC start of the call.</param>
/// <param name="Operation"><see cref="Chat"/> or <see cref="Embedding"/>.</param>
/// <param name="Items">The number of embedded texts; null for chat.</param>
/// <param name="Outcome">An <c>AiOutcome</c> code.</param>
public sealed record AiUsageEntry(DateTime OccurredAt, string Task, string Kind, string Operation, Guid ProfileRevisionId, string Model,
    int? Items, long? InputTokens, long? OutputTokens, int LatencyMs, string Outcome)
{
    public const string Chat = "chat";
    public const string Embedding = "embedding";
    /// <summary>A rerank call; <c>Items</c> is the number of scored documents.</summary>
    public const string Rerank = "rerank";
}

/// <summary>
/// The in-memory queue between the pipeline and <c>AiUsageFlusher</c>: O(1), lock-free, never blocks a call. Bounded; a
/// surplus is dropped with one warning until the next drain makes room. Losing usage (overflow, crash) loses a statistic,
/// never a function.
/// </summary>
public sealed class AiUsageRecorder
{
    public const int DefaultCapacity = 10_000;
    private readonly ConcurrentQueue<AiUsageEntry> m_entries = new();
    private readonly ILogger<AiUsageRecorder> m_logger;
    private readonly int m_capacity;
    private int m_count;
    private int m_warned;

    public AiUsageRecorder(ILogger<AiUsageRecorder> logger, int capacity = DefaultCapacity)
    {
        m_logger = logger;
        m_capacity = capacity;
    }

    public void Record(AiUsageEntry entry)
    {
        if (Interlocked.Increment(ref m_count) > m_capacity)
        {
            Interlocked.Decrement(ref m_count);
            if (Interlocked.Exchange(ref m_warned, 1) == 0)
            {
                m_logger.LogWarning("AI usage buffer is full ({Capacity} entries); further usage is dropped until the next flush", m_capacity);
            }
            return;
        }
        m_entries.Enqueue(entry);
    }

    /// <summary>Takes every recorded entry, oldest first.</summary>
    public IReadOnlyList<AiUsageEntry> Drain()
    {
        var entries = new List<AiUsageEntry>();
        while (m_entries.TryDequeue(out var entry))
        {
            entries.Add(entry);
            Interlocked.Decrement(ref m_count);
        }
        if (Volatile.Read(ref m_count) < m_capacity)
        {
            Interlocked.Exchange(ref m_warned, 0);
        }
        return entries;
    }
}
