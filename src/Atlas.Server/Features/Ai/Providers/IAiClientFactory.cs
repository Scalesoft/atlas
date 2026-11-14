using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Providers;

/// <summary>
/// Builds Microsoft.Extensions.AI clients for a saved profile revision. Everything above it (index worker, semantic search,
/// suggestions, the admin test) sees only <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>,
/// already wrapped in the Atlas pipeline (<see cref="Pipeline.AiPipeline"/>): profile defaults, the JSON schema fallback,
/// the concurrency gate, usage without content, the profile timeout and the outcome sink. Clients are cheap to build; build
/// one per operation and dispose it. The test hosts replace this with the fake provider, so no test reaches a real model.
/// </summary>
/// <remarks>Failures surface as <see cref="AiCallException"/> with an <see cref="AiOutcome"/> code and nothing else; a cancelled
/// caller gets <see cref="OperationCanceledException"/>. Nothing falls back to another provider (spec RAG §3).</remarks>
public interface IAiClientFactory
{
    /// <param name="task">The <c>AiTaskCatalog</c> code: the purpose recorded in <c>AiUsage</c> and, for <c>index.embed</c>,
    /// the background share of the concurrency gate. Explicit, never ambient.</param>
    /// <exception cref="AiConfigurationException">The profile is not a chat or quick profile, or the task is unknown.</exception>
    IChatClient Chat(AiProfileSnapshot profile, string task);

    /// <exception cref="AiConfigurationException">The profile is not an OpenAI-compatible embeddings profile (Anthropic has no
    /// embeddings), or the task is unknown.</exception>
    IEmbeddingGenerator<string, Embedding<float>> Embeddings(AiProfileSnapshot profile, string task);

    /// <exception cref="AiConfigurationException">The profile is not an OpenAI-compatible rerank profile, or the task is unknown.</exception>
    IRerankClient Rerank(AiProfileSnapshot profile, string task);

    /// <summary>List model metadata for a draft connection, without selecting or invoking a model. Bounded to five seconds.</summary>
    Task<AiModelCatalogue> ListModelsAsync(AiProfileSnapshot connection, CancellationToken ct);

    /// <summary>Whether the profile answers, without content and without spending tokens: <c>GET {base}/models</c> for
    /// OpenAI-compatible servers (judged by <see cref="AiProbeResult.FromModelList"/>), the model's entry for Anthropic.
    /// Timeout min(profile, 5 s). Never throws for an outage;
    /// not recorded as usage and not reported to the sink (the A3 probe runner decides what a result means).</summary>
    Task<AiProbeResult> ProbeAsync(AiProfileSnapshot profile, CancellationToken ct);
}

/// <param name="Ok">The server answered and lists the model (Gateway: lists any model).</param>
/// <param name="Code">The <see cref="AiOutcome"/> of a failure; <see cref="AiOutcome.ModelNotFound"/> when the server answers
/// without the model.</param>
/// <param name="ServedModel">Gateway only: the model the server lists when it does not list the configured one.</param>
public sealed record AiProbeResult(bool Ok, string? Code, bool ModelListed, string? ServedModel = null)
{
    /// <summary>The verdict on a server's model list. Gateway serves whatever model its operator loaded and ignores the
    /// request's <c>model</c>, so a profile naming another model still works: available, with the served model for the
    /// admin's warning. Every other provider rejects an unknown model, so it stays <see cref="AiOutcome.ModelNotFound"/>.</summary>
    public static AiProbeResult FromModelList(AiProfileSnapshot profile, IReadOnlyList<string> listed)
    {
        if (listed.Contains(profile.Model, StringComparer.Ordinal))
        {
            return new AiProbeResult(true, null, true);
        }
        return profile.Provider == AiProvider.Gateway && listed.Count > 0
            ? new AiProbeResult(true, null, false, listed[0])
            : new AiProbeResult(false, AiOutcome.ModelNotFound, false);
    }
}

/// <summary>Hears the outcome of every model call (not probes, not calls the gate rejected). The circuit breaker
/// (<see cref="Availability.AiAvailability"/>) implements it.</summary>
public interface IAiOutcomeSink
{
    void Success(AiProfileSnapshot profile);

    /// <param name="code">An <see cref="AiOutcome"/>; <see cref="AiOutcome.IsAvailabilityFailure"/> tells which ones count.</param>
    void Failure(AiProfileSnapshot profile, string code);
}

public sealed record AiModelOption(string Id, string Name);
public sealed record AiModelCatalogue(bool Ok, string? Code, IReadOnlyList<AiModelOption> Models);
