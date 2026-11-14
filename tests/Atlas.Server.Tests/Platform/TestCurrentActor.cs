namespace Atlas.Server.Tests.Platform;

public sealed class TestCurrentActor : ICurrentActor
{
    private readonly TestActorHolder m_holder;

    public TestCurrentActor(TestActorHolder holder)
    {
        m_holder = holder;
    }

    public Task<UserAccount> RequireAsync(CancellationToken ct)
        => Task.FromResult(m_holder.Current ?? throw new ApiException(401, "Přihlaste se."));

    public ClaimedActor Claimed()
        => m_holder.Current is { } actor ? new ClaimedActor(actor.Id, actor.SecurityStamp) : throw new ApiException(401, "Přihlaste se.");
}
