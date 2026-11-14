using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// The provider boundary of a chat call: applies the timeout (the profile's, or the task's when shorter, plan phase 3 R6), tells
/// <see cref="IAiOutcomeSink"/> (A3's circuit breaker) the outcome, and replaces any provider failure with <see cref="AiCallException"/> carrying only the code. SDK exceptions
/// carry upstream bodies in their messages, so none of them travels further up, and the warning names the task, the profile
/// kind, the host and the code only. A caller's own cancellation is not an outcome of the profile and is passed through.
/// </summary>
public sealed class AvailabilityReportingChatClient(IChatClient inner, AiProfileSnapshot profile, string task, IAiOutcomeSink outcomes,
    TimeProvider clock, ILogger logger) : NonStreamingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var call = new ProviderCall(profile, task, outcomes, clock, logger, cancellationToken);
        try
        {
            var response = await base.GetResponseAsync(messages, options, call.Token);
            call.Succeeded();
            return response;
        }
        catch (Exception exception)
        {
            throw call.Failed(exception);
        }
    }
}

/// <summary>The provider boundary of an embedding call; see <see cref="AvailabilityReportingChatClient"/>.</summary>
public sealed class AvailabilityReportingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, AiProfileSnapshot profile, string task,
    IAiOutcomeSink outcomes, TimeProvider clock, ILogger logger) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var call = new ProviderCall(profile, task, outcomes, clock, logger, cancellationToken);
        try
        {
            var embeddings = await base.GenerateAsync(values, options, call.Token);
            call.Succeeded();
            return embeddings;
        }
        catch (Exception exception)
        {
            throw call.Failed(exception);
        }
    }
}

/// <summary>One call under the profile timeout (on the injected clock, so a fake clock drives it in tests).</summary>
internal sealed class ProviderCall : IDisposable
{
    private readonly AiProfileSnapshot m_profile;
    private readonly string m_task;
    private readonly IAiOutcomeSink m_outcomes;
    private readonly ILogger m_logger;
    private readonly CancellationToken m_caller;
    private readonly CancellationTokenSource m_timeout;
    private readonly CancellationTokenSource m_linked;

    public ProviderCall(AiProfileSnapshot profile, string task, IAiOutcomeSink outcomes, TimeProvider clock, ILogger logger, CancellationToken caller)
    {
        m_profile = profile;
        m_task = task;
        m_outcomes = outcomes;
        m_logger = logger;
        m_caller = caller;
        m_timeout = new CancellationTokenSource(Limit(profile, task), clock);
        m_linked = CancellationTokenSource.CreateLinkedTokenSource(caller, m_timeout.Token);
    }

    public CancellationToken Token => m_linked.Token;

    /// <summary>The profile's timeout, or the task's own when shorter (<see cref="Settings.AiTaskDefinition.Timeout"/>: 30 s for an
    /// answer, while the chat profile defaults to 120 s). Either expiry is a <see cref="AiOutcome.Timeout"/> of the profile, except a
    /// task's latency budget (<see cref="Settings.AiTaskDefinition.LatencyBudget"/>).</summary>
    public static TimeSpan Limit(AiProfileSnapshot profile, string task)
        => Settings.AiTaskCatalog.Find(task)?.Timeout is { } own && own < profile.Timeout ? own : profile.Timeout;

    public void Succeeded() => m_outcomes.Success(m_profile);

    /// <summary>The exception to throw instead: the caller's cancellation as such, anything else as its code.</summary>
    public Exception Failed(Exception exception)
    {
        if (m_caller.IsCancellationRequested)
        {
            return new OperationCanceledException(m_caller);
        }
        var code = m_timeout.IsCancellationRequested && exception is OperationCanceledException
            ? AiOutcome.Timeout
            : AiErrorClassifier.Classify(exception, m_caller);
        // A latency budget that runs out says the task was too slow, not that the server is down (AI4-7): no breaker verdict.
        if (code != AiOutcome.Timeout || Settings.AiTaskCatalog.Find(m_task)?.LatencyBudget != true)
        {
            m_outcomes.Failure(m_profile, code);
        }
        m_logger.LogWarning("AI call {Task} on the {Kind} profile at {Host} failed: {Outcome}", m_task, m_profile.Kind, m_profile.Host, code);
        return code == AiOutcome.InvalidOutput ? new AiInvalidOutputException() : new AiCallException(code);
    }

    public void Dispose()
    {
        m_linked.Dispose();
        m_timeout.Dispose();
    }
}
