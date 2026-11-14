using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.AiIndex.Contracts;

namespace Atlas.Server.Features.AiIndex.Commands;

/// <summary><c>POST /api/admin/ai/index/generations</c>.</summary>
public sealed record StartIndexGeneration(StartIndexGenerationRequest Request);

/// <summary>Step 1 of <c>POST …/generations/{id}/verify</c>: WikiAdmin, completeness (missing pages are enqueued on the way),
/// sample titles and the revision that embeds the check queries. The model is called after this transaction.</summary>
public sealed record VerifyIndexGeneration(Guid GenerationId);

public sealed record IndexVerificationPlan(Guid GenerationId, bool Building, AiProfileSnapshot Profile, int BoundaryTasks, int MissingPages,
    int StalePages, bool DimensionsOk, IReadOnlyList<IndexSample> Samples);

public sealed record IndexSample(Guid PageId, string Title);

/// <summary><c>POST …/generations/{id}/activate</c>: Building → Active under the AI settings lock with compare-and-swap.</summary>
public sealed record ActivateIndexGeneration(Guid GenerationId, ActivateIndexGenerationRequest Request);

/// <summary><c>POST …/generations/{id}/cancel</c>: Building → Failed; the active index stays.</summary>
public sealed record CancelIndexGeneration(Guid GenerationId);

/// <summary>The hourly consistency check of the live generations (IndexMaintenance).</summary>
public sealed record RunIndexMaintenance(DateTime Now);

/// <summary>One batch of removing dead generations: Failed, and Retired before <paramref name="RetiredBefore"/> (24 h, O12).</summary>
public sealed record PurgeIndexGenerations(DateTime RetiredBefore, int Batch);

public sealed record PurgedIndexGenerations(int Pages, int Generations);
