namespace Atlas.Server.Tests.Platform;

/// <summary>Test double for handler tests of grant logic that are not about fresh re-authentication; the server has no such guard.
/// The real guard is covered by ReauthTests and the HTTP canaries.</summary>
internal sealed class PermitSecurityChanges : ISecurityChangeGuard
{
    public Task RequireAsync(UserAccount actor, SecurityChange change, CancellationToken ct) => Task.CompletedTask;
}
