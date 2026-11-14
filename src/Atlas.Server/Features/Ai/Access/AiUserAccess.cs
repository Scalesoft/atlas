using Wolverine;

namespace Atlas.Server.Features.Ai.Access;

/// <summary>Request-scoped entry gate. The transaction handler locks the current account and effective memberships.</summary>
public sealed class AiUserAccess(IMessageBus bus)
{
    public async Task<bool> AllowedAsync(CancellationToken ct) => (await PolicyAsync(ct)).Enabled;

    /// <summary>The whole resolved policy of the current account: access, the "AI s nástroji" right and the limits.</summary>
    public Task<AiResolvedPolicy> PolicyAsync(CancellationToken ct) => bus.InvokeAsync<AiResolvedPolicy>(new ReadAiPermission(), ct);
}

public sealed class ReadAiPermissionHandler(IContentAccess access, AiPolicyResolver resolver)
{
    public async Task<AiResolvedPolicy> Handle(ReadAiPermission command, CancellationToken ct)
        => await resolver.ResolveAsync((await access.LockActorAsync(null, ct)).Id, ct);
}

public sealed class GetMyAiPolicyHandler(IContentAccess access, AiPolicyResolver resolver)
{
    public async Task<MyAiPolicyView> Handle(GetMyAiPolicy command, CancellationToken ct)
        => await resolver.ViewAsync((await access.LockActorAsync(null, ct)).Id, ct);
}