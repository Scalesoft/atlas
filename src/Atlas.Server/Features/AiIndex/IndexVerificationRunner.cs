using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.AiIndex.Queries;
using Wolverine;

namespace Atlas.Server.Features.AiIndex;

/// <summary>
/// <c>POST /api/admin/ai/index/generations/{id}/verify</c>: completeness in one transaction, the check queries embedded with
/// the generation's revision outside any transaction (they spend tokens), the hits in a second one. Without a usable
/// embeddings profile the check cannot run: 503 <c>ai_unavailable</c>.
/// </summary>
public sealed class IndexVerificationRunner(IMessageBus bus, IAiClientFactory factory)
{
    /// <summary>O10: at least 80 % of the check queries find their page among the five nearest.</summary>
    public const double RequiredHitRate = 0.8;

    public async Task<GenerationVerification> VerifyAsync(Guid generationId, CancellationToken ct)
    {
        var plan = await bus.InvokeAsync<IndexVerificationPlan>(new VerifyIndexGeneration(generationId), ct);
        var queries = new List<IndexSampleQuery>(plan.Samples.Count);
        if (plan.Samples.Count > 0)
        {
            try
            {
                using var generator = factory.Embeddings(plan.Profile, AiTaskCatalog.IndexEmbed);
                foreach (var batch in plan.Samples.Chunk(IndexRunner.EmbeddingBatch))
                {
                    var embeddings = await generator.GenerateAsync(batch.Select(x => plan.Profile.Prepare(x.Title, AiEmbeddingPurpose.Query)), cancellationToken: ct);
                    queries.AddRange(batch.Select((sample, i) => new IndexSampleQuery(sample.PageId, VectorText.Format(embeddings[i].Vector.Span))));
                }
            }
            catch (Exception exception) when (exception is AiCallException or AiConfigurationException)
            {
                throw AiGate.Unavailable();
            }
        }
        var sanity = queries.Count == 0
            ? new IndexSanity(0, 0)
            : await bus.InvokeAsync<IndexSanity>(new ScoreIndexVerification(plan.GenerationId, queries), ct);
        var complete = plan.BoundaryTasks == 0 && plan.MissingPages == 0;
        var canActivate = plan.Building && complete && plan.DimensionsOk && sanity.HitsAt5 >= sanity.Checked * RequiredHitRate;
        return new GenerationVerification(complete, plan.MissingPages, plan.StalePages, plan.DimensionsOk, sanity, canActivate);
    }
}
