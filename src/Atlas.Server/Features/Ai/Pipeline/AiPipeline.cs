using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Usage;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// Wraps a provider client in Atlas's delegating clients. From the caller inwards:
/// <list type="number">
/// <item><see cref="ProfileDefaultsChatClient"/> (chat): reasoning effort and temperature from the profile when the caller set none;</item>
/// <item><see cref="ConcurrencyGateChatClient"/> / <see cref="ConcurrencyGateEmbeddingGenerator"/>: the profile's slots, interactive
/// calls never wait (a rejection is neither usage nor a failure);</item>
/// <item><see cref="UsageRecordingChatClient"/> / <see cref="UsageRecordingEmbeddingGenerator"/>: one <c>AiUsage</c> entry per call,
/// never content;</item>
/// <item><see cref="PromptSchemaChatClient"/> (chat) / <see cref="NormalizingEmbeddingGenerator"/>: the JSON schema fallback and
/// JSON extraction, or L2 normalization and the dimension check. Below usage, so an unusable answer is recorded as
/// <see cref="AiOutcome.InvalidOutput"/>;</item>
/// <item><see cref="AvailabilityReportingChatClient"/> / <see cref="AvailabilityReportingEmbeddingGenerator"/>: the profile timeout,
/// the outcome to <see cref="IAiOutcomeSink"/>, and every failure reduced to <see cref="AiCallException"/> with a code;</item>
/// <item>the provider client.</item>
/// </list>
/// The production factory and the test fake share it, so tests exercise exactly this pipeline.
/// </summary>
public sealed class AiPipeline(AiUsageRecorder usage, IAiOutcomeSink outcomes, AiConcurrencyGate gate, TimeProvider clock, ILoggerFactory logs)
{
    private readonly ILogger m_logger = logs.CreateLogger<AiPipeline>();

    public IChatClient Chat(IChatClient provider, AiProfileSnapshot profile, string task)
    {
        RequireChat(profile, task);
        IChatClient client = new AvailabilityReportingChatClient(provider, profile, task, outcomes, clock, m_logger);
        client = new PromptSchemaChatClient(client, profile);
        client = new UsageRecordingChatClient(client, profile, task, usage, clock);
        client = new ConcurrencyGateChatClient(client, profile, task, gate);
        return new ProfileDefaultsChatClient(client, profile);
    }

    public IEmbeddingGenerator<string, Embedding<float>> Embeddings(IEmbeddingGenerator<string, Embedding<float>> provider, AiProfileSnapshot profile, string task)
    {
        RequireEmbeddings(profile, task);
        IEmbeddingGenerator<string, Embedding<float>> generator = new AvailabilityReportingEmbeddingGenerator(provider, profile, task, outcomes, clock, m_logger);
        generator = new NormalizingEmbeddingGenerator(generator, profile);
        generator = new UsageRecordingEmbeddingGenerator(generator, profile, task, usage, clock);
        return new ConcurrencyGateEmbeddingGenerator(generator, profile, task, gate);
    }

    public IRerankClient Rerank(IRerankClient provider, AiProfileSnapshot profile, string task)
    {
        RequireRerank(profile, task);
        return new PipelineRerankClient(provider, profile, task, gate, usage, outcomes, clock, m_logger);
    }

    /// <exception cref="AiConfigurationException">Not an OpenAI-compatible rerank profile with a URL, or an unknown task.</exception>
    public static void RequireRerank(AiProfileSnapshot profile, string task)
    {
        RequireTask(task);
        if (profile.Kind != AiProfileKind.Rerank || profile.Provider != AiProvider.OpenAiCompatible)
        {
            throw new AiConfigurationException($"The {profile.Kind} profile does not rerank.");
        }
        if (profile.BaseUrl is null)
        {
            throw new AiConfigurationException("An OpenAI-compatible profile needs a URL.");
        }
    }

    /// <exception cref="AiConfigurationException">Not a chat or quick profile, or an unknown task.</exception>
    public static void RequireChat(AiProfileSnapshot profile, string task)
    {
        RequireTask(task);
        if (profile.Kind is not (AiProfileKind.Chat or AiProfileKind.Quick))
        {
            throw new AiConfigurationException($"The {profile.Kind} profile does not chat.");
        }
        if (!AiProvider.IsKnown(profile.Provider))
        {
            throw new AiConfigurationException($"Unknown AI provider {profile.Provider}.");
        }
        if (!AiProvider.SupportsProfile(profile.Provider, profile.Location, profile.Kind))
        {
            throw new AiConfigurationException("Gateway needs a company server.");
        }
        if (AiProvider.UsesOpenAiProtocol(profile.Provider) && profile.BaseUrl is null)
        {
            throw new AiConfigurationException("An OpenAI-compatible profile needs a URL.");
        }
    }

    /// <exception cref="AiConfigurationException">Not an OpenAI-compatible embeddings profile with a URL, or an unknown task.</exception>
    public static void RequireEmbeddings(AiProfileSnapshot profile, string task)
    {
        RequireTask(task);
        if (profile.Provider == AiProvider.Anthropic)
        {
            throw new AiConfigurationException("Anthropic provides no embeddings; use LM Studio or OpenAI.");
        }
        if (profile.Kind != AiProfileKind.Embeddings || profile.Provider != AiProvider.OpenAiCompatible)
        {
            throw new AiConfigurationException($"The {profile.Kind} profile does not embed.");
        }
        if (profile.BaseUrl is null)
        {
            throw new AiConfigurationException("An OpenAI-compatible profile needs a URL.");
        }
    }

    private static void RequireTask(string task)
    {
        if (AiTaskCatalog.Find(task) is null)
        {
            throw new AiConfigurationException($"Unknown AI task {task}.");
        }
    }
}
