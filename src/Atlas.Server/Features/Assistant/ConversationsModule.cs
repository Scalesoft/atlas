using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Queries;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Assistant;

public static class ConversationsModule
{
    /// <summary>Q4: reading history with revalidation, deletion and the retention runner with its hourly job (the job runs by the
    /// scheduler of the server; tests call <see cref="AiConversationMaintenanceRunner"/>). Needs AddAssistantSources (the source
    /// registry) and AddAi (the configuration cache).</summary>
    public static IServiceCollection AddAiConversations(this IServiceCollection services)
    {
        services.AddScoped<ConversationReader>();
        services.AddScoped<AiConversationDeletion>();
        services.AddSingleton<AiConversationMaintenanceRunner>();
        services.AddJob<AiConversationMaintenance>();
        return services;
    }
}
