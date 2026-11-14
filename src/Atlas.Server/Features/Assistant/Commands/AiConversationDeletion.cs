using Atlas.Server.Features.Assistant.Persistence;

namespace Atlas.Server.Features.Assistant.Commands;

/// <summary>
/// Data of another feature that refers to messages and must change before conversations go, in the deleting transaction (plan
/// phase 3 O20: the feedback of a deleted conversation stays anonymously, its private note is deleted). Called with the ids of the
/// conversations about to be deleted, whose messages still exist. Every deletion path calls it: one conversation, all of an owner
/// and the retention purge.
/// </summary>
public interface IAiConversationDeleting
{
    Task DeletingAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken ct);
}

/// <summary>Deletes conversations with their messages and dependencies (cascade), never a page or revision, after every
/// <see cref="IAiConversationDeleting"/> has run. The caller holds the conversations locked (FOR UPDATE).</summary>
public sealed class AiConversationDeletion(ConversationStore store, IEnumerable<IAiConversationDeleting> listeners)
{
    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
        {
            return 0;
        }
        foreach (var listener in listeners)
        {
            await listener.DeletingAsync(conversationIds, ct);
        }
        return await store.DeleteAsync(conversationIds, ct);
    }
}
