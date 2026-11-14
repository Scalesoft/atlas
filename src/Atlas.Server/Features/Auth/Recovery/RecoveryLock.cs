namespace Atlas.Server.Features.Auth.Recovery;

/// <summary>
/// The advisory key that serializes issuing and redeeming the administrator recovery token. Both
/// handlers must name the same key, so it lives in one place; the value itself never changes.
/// </summary>
internal static class RecoveryLock
{
    public const long Key = 731145015;
}
