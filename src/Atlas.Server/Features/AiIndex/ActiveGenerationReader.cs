using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.AiIndex.Domain;

namespace Atlas.Server.Features.AiIndex;

/// <summary>A3's read of the active generation (<see cref="IActiveGenerationReader"/>, for the configuration cache and the
/// capabilities) and the reads over the index (V3–V5): the generation queries are answered from.</summary>
public sealed class ActiveGenerationReader(IndexStore store) : IActiveGenerationReader
{
    public async Task<AiActiveGeneration?> ReadAsync(CancellationToken ct)
    {
        var active = (await store.GenerationsAsync([IndexGenerationState.Active], ct)).FirstOrDefault();
        return active is null ? null : new AiActiveGeneration(active.Id, active.Fingerprint, active.ProfileRevisionId);
    }
}

/// <summary>A1's hook (<see cref="IEmbeddingChangeGuard"/>): while a generation is active, an embeddings revision with another
/// fingerprint answers 409 <c>ai_generation_required</c>; a key rotation keeps the fingerprint and passes.</summary>
public sealed class ActiveIndexGenerationGuard(IActiveGenerationReader reader) : IEmbeddingChangeGuard
{
    public async Task<string?> ActiveFingerprintAsync(CancellationToken ct) => (await reader.ReadAsync(ct))?.Fingerprint;
}

/// <summary>The index tile of A3's overview (<see cref="IAiIndexOverview"/>); null while no generation is live (the client shows
/// "Nepostaven"). The caller has decided that the actor is a WikiAdmin.</summary>
public sealed class AiIndexOverview(IndexViews views) : IAiIndexOverview
{
    public Task<AiIndexSummary?> ReadAsync(CancellationToken ct) => views.SummaryAsync(ct);
}
