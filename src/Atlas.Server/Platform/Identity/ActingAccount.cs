namespace Atlas.Server.Platform.Identity;

/// <summary>
/// The account a server-side job acts as, with no request (the automatic trash cleanup acts as the admin who switched it on): for the
/// async flow inside <see cref="Use"/>, <see cref="HttpCurrentActor"/> claims it instead of a sign-in, and the channel stays system
/// (no HttpContext). Every right, lock and audit then is that account's, checked as for its own request; a stamp that changed since
/// (a password reset, a deactivation) fails as a stale sign-in would. Only server code sets it; no request can.
/// </summary>
public static class ActingAccount
{
    private static readonly AsyncLocal<ClaimedActor?> s_current = new();
    private static readonly AsyncLocal<RequestChannel?> s_channel = new();

    internal static ClaimedActor? Current => s_current.Value;

    /// <summary>The channel of the work done as the account, when not system: the server's checkpoint of what people typed in their
    /// browsers (3.6-H) is web.</summary>
    internal static RequestChannel? Channel => s_channel.Value;

    public static IDisposable Use(Guid accountId, string? securityStamp, RequestChannel? channel = null)
    {
        var previous = (s_current.Value, s_channel.Value);
        s_current.Value = new ClaimedActor(accountId, securityStamp);
        s_channel.Value = channel;
        return new Restore(previous);
    }

    private sealed class Restore((ClaimedActor?, RequestChannel?) previous) : IDisposable
    {
        public void Dispose() => (s_current.Value, s_channel.Value) = previous;
    }
}
