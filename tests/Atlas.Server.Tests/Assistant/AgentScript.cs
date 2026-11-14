using Microsoft.Extensions.AI;
using Atlas.Server.Features.Assistant.Agent;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Fake model scripts of agent turns.</summary>
internal static class AgentScript
{
    /// <summary>The script sees the history without the turn's starting search (<see cref="AssistantAgentRunner.StartingCallId"/>), as
    /// if the turn began with the model's own first call: for tests about the tool loop, not about the starting context.</summary>
    public static Func<IReadOnlyList<ChatMessage>, ChatMessage?> WithoutStartingSearch(Func<IReadOnlyList<ChatMessage>, ChatMessage?> script)
        => messages => script(messages.Where(message => !IsStartingSearch(message)).ToArray());

    public static bool IsStartingSearch(ChatMessage message)
        => message.Contents.Any(content => content is FunctionCallContent { CallId: AssistantAgentRunner.StartingCallId }
            or FunctionResultContent { CallId: AssistantAgentRunner.StartingCallId });
}
