using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Queries;

namespace Atlas.Server.Features.Assistant.Commands;

/// <summary>S6: no model call, no AI gate; the stored answer is revalidated like any read.</summary>
public sealed class ContinueAiConversationHandler(IContentAccess access, ConversationStore store, ConversationReader reader, AiConfigurationCache configuration)
{
    public async Task<AiConversationView> Handle(ContinueAiConversation command, CancellationToken ct)
    {
        var snapshot = await configuration.GetAsync(ct);
        var actor = await access.LockActorAsync(null, ct);
        if (!await store.SetListedAsync(command.ConversationId, actor.Id, ct))
        {
            throw ConversationReader.NotFound();
        }
        return await reader.ViewAsync(actor, command.ConversationId, snapshot, ct) ?? throw ConversationReader.NotFound();
    }
}

/// <summary>Deleting is the user's right and works without AI.</summary>
public sealed class DeleteAiConversationHandler(IContentAccess access, ConversationStore store, AiConversationDeletion deletion)
{
    public async Task Handle(DeleteAiConversation command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        if (await store.LockOwnedAsync(command.ConversationId, actor.Id, ct) is null)
        {
            throw ConversationReader.NotFound();
        }
        await deletion.DeleteAsync([command.ConversationId], ct);
    }

    public async Task Handle(DeleteAllAiConversations command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        await deletion.DeleteAsync(await store.LockAllOwnedAsync(actor.Id, ct), ct);
    }
}

/// <summary>The retention of <see cref="AiConversationMaintenanceRunner"/>, one short transaction per message.</summary>
public sealed class AiConversationRetentionHandler(ConversationStore store, AiConversationDeletion deletion)
{
    public async Task<CanceledAiTurns> Handle(CancelStaleAiTurns command, CancellationToken ct)
        => new(await store.CancelStaleAsync(command.PreparedBefore, command.At, ct));

    public async Task<PurgedAiConversations> Handle(PurgeAiConversations command, CancellationToken ct)
        => new(await deletion.DeleteAsync(await store.LockExpiredAsync(command.UnlistedBefore, command.ListedBefore, command.Batch, ct), ct));
}
