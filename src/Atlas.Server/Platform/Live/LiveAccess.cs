namespace Atlas.Server.Platform.Live;

/// <summary>
/// A channel's decision for a connection: NotFound when the resource is missing, trashed or not readable by the account (no
/// difference, as HTTP answers 404 for all three), Forbidden when the connection's session no longer proves the account
/// (inactive, stamp rotated, browser session revoked), Allowed with whether it may write and the channel's current sequence.
/// </summary>
public sealed record LiveAccess(LiveAccessOutcome Outcome, bool CanWrite, long Sequence)
{
    public static LiveAccess NotFound { get; } = new(LiveAccessOutcome.NotFound, false, 0);

    public static LiveAccess Forbidden { get; } = new(LiveAccessOutcome.Forbidden, false, 0);

    public bool Allowed => Outcome == LiveAccessOutcome.Allowed;
}

public enum LiveAccessOutcome
{
    NotFound,
    Forbidden,
    Allowed
}
