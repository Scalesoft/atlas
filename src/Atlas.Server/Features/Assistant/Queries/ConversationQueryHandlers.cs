using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Assistant.Contracts;

namespace Atlas.Server.Features.Assistant.Queries;

/// <summary>"Moje konverzace": works without AI (návrh §2a point 7), only under the readers' locks.</summary>
public sealed class ListAiConversationsHandler(IContentAccess access, ConversationReader reader)
{
    public async Task<AiConversationList> Handle(ListAiConversations query, CancellationToken ct)
    {
        // The readers' protocol: the shared content lock and the account FOR SHARE; a revoked credential answers 401.
        var actor = await access.LockActorAsync(null, ct);
        return await reader.ListAsync(actor, query.Before?.UtcDateTime, ct);
    }
}

public sealed class GetAiConversationHandler(IContentAccess access, ConversationReader reader, AiConfigurationCache configuration)
{
    public async Task<AiConversationView> Handle(GetAiConversation query, CancellationToken ct)
    {
        // From memory, before any lock (R5 needs the current chat profile only to say whether the conversation may continue).
        var snapshot = await configuration.GetAsync(ct);
        var actor = await access.LockActorAsync(null, ct);
        return await reader.ViewAsync(actor, query.ConversationId, snapshot, ct) ?? throw ConversationReader.NotFound();
    }
}
