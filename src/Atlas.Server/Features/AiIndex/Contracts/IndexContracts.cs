using Atlas.Server.Features.Ai.Settings.Contracts;

namespace Atlas.Server.Features.AiIndex.Contracts;

/// <summary><c>GET /api/admin/ai/index</c>: the live generations, those retired within the last 24 hours and the queue of
/// both live generations. WikiAdmin only, so page titles may appear.</summary>
public sealed record IndexStatus(GenerationView? Active, GenerationView? Building, IReadOnlyList<GenerationView> Retired, IndexQueue Queue);

/// <param name="Pending">Every task of a live generation, including failing ones.</param>
/// <param name="Failing">Tasks whose last attempt failed for a reason other than an outage (they back off up to 15 minutes).</param>
/// <param name="OldestPendingAt">When the oldest waiting task was enqueued.</param>
public sealed record IndexQueue(int Pending, int Failing, DateTime? OldestPendingAt);

/// <param name="State"><c>Building</c>, <c>Active</c>, <c>Retired</c> or <c>Failed</c>.</param>
/// <param name="EligiblePages">Pages that belong in the index now: published, not in the trash, in a space included in AI;
/// templates count (their title only).</param>
/// <param name="IndexedPages">Eligible pages whose row in this generation holds their current published revision.</param>
/// <param name="Missing">At most 20 eligible pages that are not indexed with their current revision yet, by title.</param>
/// <param name="Version">Compare-and-swap token of activation; changes with the generation's state, not with progress.</param>
public sealed record GenerationView(Guid Id, string State, string Model, string? ModelVersion, string ChunkingVersion, int EligiblePages,
    int IndexedPages, int Chunks, int PendingTasks, int FailingTasks, IReadOnlyList<MissingPage> Missing, DateTime CreatedAt,
    DateTime? ActivatedAt, string Version);

public sealed record MissingPage(Guid PageId, string Title);

/// <param name="Mode"><c>build</c> (the first index from the saved embeddings profile), <c>rebuild</c> (the same profile, a
/// new generation) or <c>replace</c> (a new model: <paramref name="Profile"/> becomes a candidate revision without an
/// assignment; activation assigns it).</param>
public sealed record StartIndexGenerationRequest(string? Mode, SaveAiProfileRequest? Profile, Guid? SavedProfileId = null,
    string? ExpectedSavedVersion = null, string? ExpectedProfileVersion = null)
{
    public override string ToString() => $"StartIndexGenerationRequest {{ Mode = {Mode} }}";
}

public sealed record ActivateIndexGenerationRequest(string? ExpectedVersion);

/// <param name="Complete">No task enqueued at or before the generation's boundary is left and every eligible page has a row.</param>
/// <param name="MissingPages">Eligible pages without a row in the generation.</param>
/// <param name="StalePages">Rows of an older revision or of a page that no longer belongs in the index; they are being caught up
/// and do not block activation.</param>
/// <param name="Sanity">Check queries: titles of up to 50 random indexed pages, embedded as queries; a hit is the page among the
/// five nearest pages. Spends tokens.</param>
/// <param name="CanActivate">A building generation that is complete, of the right dimension, with at least 80 % hits (O10).</param>
public sealed record GenerationVerification(bool Complete, int MissingPages, int StalePages, bool DimensionsOk, IndexSanity Sanity, bool CanActivate);

public sealed record IndexSanity(int Checked, int HitsAt5);

/// <summary>What one consistency check found and enqueued across the live generations: eligible pages without a row, rows of an
/// older revision, rows of pages that no longer belong in the index. Pages that already had a task are not counted.</summary>
public sealed record IndexConsistency(DateTime CheckedAt, int Missing, int Stale, int Orphaned);
