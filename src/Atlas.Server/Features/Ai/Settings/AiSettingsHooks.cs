using Atlas.Server.Features.Ai.Settings.Contracts;

namespace Atlas.Server.Features.Ai.Settings;

/// <summary>
/// Told after a change of the AI configuration has committed (never after a rollback), so the in-process configuration cache
/// (<see cref="AiConfigurationCache"/>, which implements it) drops its snapshot instead of waiting for its periodic refresh:
/// a save in Administrace → AI, and the activation of an index generation (V2). A changed profile revision then starts
/// unverified and is probed at once (A3).
/// </summary>
public interface IAiConfigurationChanged
{
    void Changed();
}

/// <summary>
/// The embedding index's say in a profile save (R3). While an index generation is active, a revision of the embeddings
/// profile with another fingerprint would make queries embed with a model the index was not built with, so the save
/// answers <c>409 ai_generation_required</c> and the change goes through the model-exchange flow instead. The index module
/// implements it (<c>ActiveIndexGenerationGuard</c>) with a read of its generation table.
/// </summary>
public interface IEmbeddingChangeGuard
{
    /// <summary>The fingerprint of the active index generation, or null when there is none.</summary>
    Task<string?> ActiveFingerprintAsync(CancellationToken ct);
}

/// <summary>The index generation queries are answered from: its fingerprint must match the current embeddings revision's for
/// any embedding capability (A3, R3).</summary>
/// <param name="ProfileRevisionId">The embeddings revision the generation was built with.</param>
public sealed record AiActiveGeneration(Guid Id, string Fingerprint, Guid ProfileRevisionId);

/// <summary>
/// The configuration cache's read of the active index generation, in the same transaction as the rest of the configuration
/// (<c>LoadAiConfiguration</c>). Scoped, with the request's <c>ISession</c>. The index module implements it with a read of its
/// generation table (<c>ActiveGenerationReader</c>).
/// </summary>
public interface IActiveGenerationReader
{
    Task<AiActiveGeneration?> ReadAsync(CancellationToken ct);
}

/// <summary>The index tile of Administrace → AI → Přehled (<c>GET /api/admin/ai</c>, <see cref="AiOverview.Index"/>). Scoped.
/// The index module implements it (<c>AiIndexOverview</c>).</summary>
public interface IAiIndexOverview
{
    Task<AiIndexSummary?> ReadAsync(CancellationToken ct);
}
