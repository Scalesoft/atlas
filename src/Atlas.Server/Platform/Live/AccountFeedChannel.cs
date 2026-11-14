using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// A server-derived feed with one channel, <c>{kind}:{Guid.Empty}</c>, decided by the account alone: while its session proves it
/// (<see cref="UserLiveChannel.ProvenAsync"/>) and <see cref="AllowsAsync"/> says so (an effective wiki administrator for the log and AI
/// health, any signed-in account for the AI capabilities). Its events say only "read again".
/// </summary>
public abstract class AccountFeedChannel(AtlasDatabase database) : IDerivedLiveChannel
{
    public abstract string Kind { get; }

    public static string NameOf(string kind) => kind + ":" + Guid.Empty.ToString("D");

    /// <summary>The kind's rule for an account whose session is proven; plain reads in <paramref name="session"/>.</summary>
    protected abstract Task<bool> AllowsAsync(ISession session, UserAccount actor, CancellationToken ct);

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        using ISession session = database.OpenSession();
        return resourceId == Guid.Empty && await UserLiveChannel.ProvenAsync(session, subscriber, ct) is { } actor && await AllowsAsync(session, actor, ct)
            ? new LiveAccess(LiveAccessOutcome.Allowed, false, 0)
            : LiveAccess.NotFound;
    }

    public async Task<IReadOnlyList<Guid>> ResourcesAsync(LiveSubscriber subscriber, CancellationToken ct)
    {
        return (await AuthorizeAsync(subscriber, Guid.Empty, ct)).Allowed ? [Guid.Empty] : [];
    }
}

/// <summary>An <see cref="AccountFeedChannel"/> for effective wiki administrators only (Hub's included).</summary>
public abstract class WikiAdminFeedChannel(AtlasDatabase database) : AccountFeedChannel(database)
{
    protected override Task<bool> AllowsAsync(ISession session, UserAccount actor, CancellationToken ct) => EffectiveWikiAdmin.IsAdminAsync(session, actor, ct);
}
