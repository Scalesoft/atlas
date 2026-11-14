using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiIndex;

/// <summary>The admin views of generations and the queue (Administrace → AI → Index, and the overview's index tile).</summary>
public sealed class IndexViews(ISession session, IndexStore store)
{
    public const int MissingShown = 20;

    public async Task<IndexStatus> StatusAsync(CancellationToken ct)
    {
        var generations = await store.GenerationsAsync(
            [IndexGenerationState.Building, IndexGenerationState.Active, IndexGenerationState.Retired], ct);
        GenerationView? active = null, building = null;
        var retired = new List<GenerationView>();
        foreach (var generation in generations)
        {
            var view = await ViewAsync(generation, ct);
            switch (generation.State)
            {
                case IndexGenerationState.Active:
                    active = view;
                    break;
                case IndexGenerationState.Building:
                    building = view;
                    break;
                default:
                    retired.Add(view);
                    break;
            }
        }
        return new IndexStatus(active, building, retired, await store.QueueAsync(ct));
    }

    public async Task<GenerationView> ViewAsync(IndexGenerationRow generation, CancellationToken ct)
    {
        var revision = await session.GetAsync<AiProfileRevision>(generation.ProfileRevisionId, ct);
        var stats = await store.StatsAsync(generation.Id, ct);
        var live = generation.State is IndexGenerationState.Building or IndexGenerationState.Active;
        var missing = live && stats.IndexedPages < stats.EligiblePages ? await store.MissingAsync(generation.Id, MissingShown, ct) : [];
        return new GenerationView(generation.Id, generation.State, revision?.Model ?? "", revision?.ModelVersion, generation.ChunkingVersion,
            stats.EligiblePages, stats.IndexedPages, stats.Chunks, stats.PendingTasks, stats.FailingTasks, missing, generation.CreatedAt,
            generation.ActivatedAt, AdministrationVersion.Format(generation.Version));
    }

    /// <summary>The overview tile: the building generation while one exists, else the active one; null without either (A3's
    /// "no index yet").</summary>
    public async Task<AiIndexSummary?> SummaryAsync(CancellationToken ct)
    {
        var generations = await store.GenerationsAsync([IndexGenerationState.Building, IndexGenerationState.Active], ct);
        var active = generations.FirstOrDefault(x => x.State == IndexGenerationState.Active);
        var target = generations.FirstOrDefault(x => x.State == IndexGenerationState.Building) ?? active;
        if (target is null)
        {
            return null;
        }
        var pending = await store.QueueAsync(ct);
        var queue = new AiIndexQueueSummary(pending.Pending, pending.Failing, pending.OldestPendingAt);
        var revision = await session.GetAsync<AiProfileRevision>(target.ProfileRevisionId, ct);
        var stats = await store.StatsAsync(target.Id, ct);
        return new AiIndexSummary(target.State, revision?.Model, stats.EligiblePages, stats.IndexedPages, queue,
            active is not null && !string.Equals(active.ChunkingVersion, KnowledgeChunker.Version, StringComparison.Ordinal));
    }
}
