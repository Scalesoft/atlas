using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;

namespace Atlas.Server.Features.Assistant.Queries;

/// <summary>The task of the owner's answer (<c>search.answer</c> or <c>chat.answer</c>), so the completion endpoint can ask
/// <c>AiGate</c> before anything else (plan phase 3 §7 step 1). 404 when it is not the owner's answer.</summary>
public sealed record GetAiMessageTask(Guid MessageId);

public sealed record AiMessageTask(string Task);

public sealed class GetAiMessageTaskHandler(ICurrentActor actor, ConversationStore store)
{
    public async Task<AiMessageTask> Handle(GetAiMessageTask query, CancellationToken ct)
    {
        var owned = await store.MessageAsync(query.MessageId, actor.Claimed().Id, forUpdate: false, ct);
        return owned is { Message: { Role: AiMessageRole.Assistant, Task: { } task } }
            ? new AiMessageTask(task)
            : throw new ApiException(StatusCodes.Status404NotFound, "Zpráva nebyla nalezena.");
    }
}
