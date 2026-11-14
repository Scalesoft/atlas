using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Usage;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>Records one <see cref="AiUsageEntry"/> per chat call: model (the answer's, else the profile's), tokens (null =
/// unknown), latency and outcome. Reads only counts and identifiers of the response, never its text.</summary>
public sealed class UsageRecordingChatClient(IChatClient inner, AiProfileSnapshot profile, string task, AiUsageRecorder usage, TimeProvider clock)
    : NonStreamingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = new UsageCall(profile, task, AiUsageEntry.Chat, null, usage, clock);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            call.Finish(AiOutcome.Ok, response.ModelId, response.Usage);
            return response;
        }
        catch (Exception exception)
        {
            call.Finish(AiErrorClassifier.Classify(exception, cancellationToken), null, null);
            throw;
        }
    }
}

/// <summary>Records one <see cref="AiUsageEntry"/> per embedding call, with the number of embedded texts.</summary>
public sealed class UsageRecordingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, AiProfileSnapshot profile, string task,
    AiUsageRecorder usage, TimeProvider clock) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values as IReadOnlyCollection<string> ?? values.ToArray();
        var call = new UsageCall(profile, task, AiUsageEntry.Embedding, texts.Count, usage, clock);
        try
        {
            var embeddings = await base.GenerateAsync(texts, options, cancellationToken);
            call.Finish(AiOutcome.Ok, embeddings.Count > 0 ? embeddings[0].ModelId : null, embeddings.Usage);
            return embeddings;
        }
        catch (Exception exception)
        {
            call.Finish(AiErrorClassifier.Classify(exception, cancellationToken), null, null);
            throw;
        }
    }
}

/// <summary>The start of one call and what its usage row needs once it ends.</summary>
internal sealed class UsageCall(AiProfileSnapshot profile, string task, string operation, int? items, AiUsageRecorder usage, TimeProvider clock)
{
    private const int MaxModelLength = 200;
    private readonly DateTime m_occurredAt = clock.GetUtcNow().UtcDateTime;
    private readonly long m_started = clock.GetTimestamp();

    public void Finish(string outcome, string? model, UsageDetails? tokens)
    {
        var latency = clock.GetElapsedTime(m_started).TotalMilliseconds;
        var name = string.IsNullOrWhiteSpace(model) ? profile.Model : model;
        usage.Record(new AiUsageEntry(m_occurredAt, task, profile.Kind, operation, profile.RevisionId,
            name.Length > MaxModelLength ? name[..MaxModelLength] : name, items, tokens?.InputTokenCount, tokens?.OutputTokenCount,
            (int)Math.Clamp(Math.Round(latency), 0, int.MaxValue), outcome));
    }
}
