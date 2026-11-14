using Microsoft.Extensions.AI;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// The base of every chat decorator of <see cref="AiPipeline"/>. Nothing streams a chat answer (an answer is validated whole before
/// it is released), so no decorator handles a stream. A plain <see cref="DelegatingChatClient"/> forwards one straight to the inner
/// client, past the concurrency gate, usage recording, profile defaults, the schema fallback and the availability report; here a
/// stream fails at once instead. Streaming needs every decorator rebuilt for it, not one of them skipped.
/// </summary>
public abstract class NonStreamingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The AI pipeline does not stream; use GetResponseAsync.");
}
