using Atlas.Server.Features.AiIndex.Domain;

namespace Atlas.Server.Features.AiIndex.Commands;

/// <summary>Step 1 of the worker (one short transaction): lease due tasks and read what to embed. Tasks of pages that left the
/// index, or whose row is already current, are settled right here without a model call.</summary>
public sealed record ClaimIndexTasks(int Max, TimeSpan Lease, DateTime Now);

/// <summary>When the worker's claim next finds a task, by the claim's own rules; null when none can be claimed (IndexJob.NextDueAsync).</summary>
public sealed record NextIndexTaskDue(DateTime Now)
{
    /// <summary>A generation whose embeddings profile is down is asked again after this; the outage spends no attempt.</summary>
    public static readonly TimeSpan OutageRecheck = TimeSpan.FromMinutes(1);
}

/// <param name="Sources">Tasks to embed, with the page content read in the claim transaction.</param>
/// <param name="Settled">Tasks finished in the claim (removals and rows that were current already).</param>
public sealed record ClaimedIndexWork(IReadOnlyList<IndexSource> Sources, int Settled)
{
    public static readonly ClaimedIndexWork None = new([], 0);
}

/// <summary>Step 3 (one short transaction, after the model calls): write the page into the generation if the task and the page
/// are still the ones that were claimed; otherwise only end the lease, because a newer change has enqueued the page again.</summary>
public sealed record StoreIndexedPage(Guid GenerationId, Guid PageId, DateTime Token, Guid RevisionId, bool IsTemplate, byte[] TitleHash,
    string? TitleVector, IReadOnlyList<IndexedChunk> Chunks)
{
    public override string ToString() => $"StoreIndexedPage {{ GenerationId = {GenerationId}, PageId = {PageId}, Chunks = {Chunks.Count} }}";
}

/// <param name="Written">False: the claimed state was stale and nothing was written.</param>
public sealed record StoredIndexPage(bool Written);

/// <summary>A failed task: <paramref name="CountsAsAttempt"/> false for an outage (the lease ends, no attempt is spent, the queue
/// catches up as soon as the profile is back); true for anything else (exponential backoff up to 15 minutes).</summary>
public sealed record ReleaseIndexTask(Guid GenerationId, Guid PageId, DateTime Token, string Code, bool CountsAsAttempt, DateTime Now, double Jitter);
