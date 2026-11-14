using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// The account's own channel <c>user:{accountId}</c> of the one /hubs/live connection (plan §11: the feed of a user moves to the
/// <c>user:</c> channel): only the account itself subscribes it, while its session still proves it (active, stamp, browser session).
/// The platform's, so its publishers (the inbox, the vault's security events) depend on no module of another (ARCH2-2).
/// </summary>
public sealed class UserLiveChannel(AtlasDatabase database) : ILiveChannel
{
    public const string Prefix = "user";

    public string Kind => Prefix;

    public static string Name(Guid accountId) => Prefix + ":" + accountId.ToString("D");

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        if (resourceId != subscriber.UserId)
        {
            return LiveAccess.NotFound;
        }
        using ISession session = database.OpenSession();
        return await ProvenAsync(session, subscriber, ct) is null ? LiveAccess.Forbidden : new LiveAccess(LiveAccessOutcome.Allowed, false, 0);
    }

    /// <summary>The subscriber's account while its session still proves it (active, the same stamp, the browser session not
    /// revoked); null otherwise.</summary>
    internal static async Task<UserAccount?> ProvenAsync(ISession session, LiveSubscriber subscriber, CancellationToken ct)
    {
        var account = await session.GetAsync<UserAccount>(subscriber.UserId, ct);
        return account is null || !account.IsActive || account.SecurityStamp != subscriber.SecurityStamp
            || subscriber.SessionId is { } browser && await BrowserSessionRevocations.IsRevokedAsync(session, browser, ct)
            ? null
            : account;
    }
}
