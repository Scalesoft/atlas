using System.Text.Json;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Usage;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// The pipeline of a rerank call in one client (Microsoft.Extensions.AI has no rerank abstraction to delegate to), in the order of
/// <see cref="AiPipeline"/>: the profile's slot, one <c>AiUsage</c> row (the documents as items, tokens unknown), then the timeout,
/// the outcome to the circuit breaker and every failure as its code. Scores that do not match the documents are
/// <see cref="AiOutcome.InvalidOutput"/>.
/// </summary>
internal sealed class PipelineRerankClient(IRerankClient provider, AiProfileSnapshot profile, string task, AiConcurrencyGate gate, AiUsageRecorder usage,
    IAiOutcomeSink outcomes, TimeProvider clock, ILogger logger) : IRerankClient
{
    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(profile, task, ct);
        var recorded = new UsageCall(profile, task, AiUsageEntry.Rerank, documents.Count, usage, clock);
        try
        {
            using var call = new ProviderCall(profile, task, outcomes, clock, logger, ct);
            IReadOnlyList<double> scores;
            try
            {
                scores = await provider.ScoreAsync(query, documents, call.Token);
                if (scores.Count != documents.Count || !scores.All(double.IsFinite))
                {
                    throw new JsonException("The scores do not match the documents.");
                }
                call.Succeeded();
            }
            catch (Exception exception)
            {
                throw call.Failed(exception);
            }
            recorded.Finish(AiOutcome.Ok, null, null);
            return scores;
        }
        catch (Exception exception)
        {
            recorded.Finish(AiErrorClassifier.Classify(exception, ct), null, null);
            throw;
        }
    }
}
