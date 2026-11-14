using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// The profile's reasoning effort (unset = <see cref="ReasoningEffort.None"/>, design §15) and temperature, each only where
/// the caller set nothing: a task that needs to think says so. The temperature is sent only when the profile has one,
/// because Claude 5.x refuses non-default sampling.
/// </summary>
public sealed class ProfileDefaultsChatClient(IChatClient inner, AiProfileSnapshot profile) : NonStreamingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, WithDefaults(options), cancellationToken);

    private ChatOptions WithDefaults(ChatOptions? options)
    {
        var result = options?.Clone() ?? new ChatOptions();
        result.Reasoning ??= new ReasoningOptions { Effort = profile.Effort };
        result.Temperature ??= profile.Temperature;
        return result;
    }
}
