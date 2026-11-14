using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Persistence;

namespace Atlas.Server.Features.Assistant;

/// <summary>
/// Answers from the wiki and the assistant (Atlas 2.0 phase 3): private conversations, their messages and the source
/// dependencies of every answer. Q1 registers the store; retrieval (Q2), the answer flow (Q3), reading and retention (Q4) and
/// feedback (Q5) add their services here. Without a chat profile nothing here is called, so a wiki without AI pays nothing.
/// </summary>
public static class AssistantModule
{
    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddScoped<ConversationStore>();
        services.AddScoped<AnswerTurnGate>();
        services.AddScoped<AiAnswerRunner>();
        services.AddScoped<AssistantAgentRunner>();
        services.AddScoped<AiProposalStore>();
        services.AddScoped<AssistantProposals>();
        services.AddSingleton<AiCompletionGate>();
        services.AddAiConversations();
        return services;
    }
}
