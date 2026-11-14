using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>Holds an <see cref="AiConcurrencyGate"/> slot for the duration of a chat call.</summary>
public sealed class ConcurrencyGateChatClient(IChatClient inner, AiProfileSnapshot profile, string task, AiConcurrencyGate gate) : NonStreamingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var _ = await gate.EnterAsync(profile, task, cancellationToken);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>Holds an <see cref="AiConcurrencyGate"/> slot for the duration of an embedding call.</summary>
public sealed class ConcurrencyGateEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, AiProfileSnapshot profile, string task,
    AiConcurrencyGate gate) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var _ = await gate.EnterAsync(profile, task, cancellationToken);
        return await base.GenerateAsync(values, options, cancellationToken);
    }
}
