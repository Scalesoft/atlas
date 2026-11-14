using Atlas.Server.Platform.Live;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// Live security notifications (WP4 V5) over SignalR, never polling (owner rule): an access request changed, or a released secret
/// value may no longer be shown. They reach the account's own channel <c>user:{id}</c> on the tab's one live connection (/hubs/live),
/// beside its inbox signal, so a reader holds no second connection for them. Payloads carry identifiers and a state only, never a
/// value, a reason or a name; the client reloads what it needs through the authorized API. Sent only after the commit that caused them (AccessChanges.AfterCommit) or by the release watch.
/// </summary>
public interface ISecurityEvents
{
    Task AccessRequestChangedAsync(IReadOnlyCollection<Guid> userIds, AccessRequestChanged change);

    Task SecretAccessRevokedAsync(Guid userId, SecretAccessRevoked revoked);
}

/// <summary>Event accessRequestChanged: State pending, approved, rejected or cancelled.</summary>
public sealed record AccessRequestChanged(Guid RequestId, Guid? SecretId, Guid VaultId, string State);

/// <summary>Event secretAccessRevoked: hide any value of this secret now; the next release re-authorizes.</summary>
public sealed record SecretAccessRevoked(Guid SecretId);

/// <summary>Each account's events on its own channel <c>user:{id}</c> of /hubs/live (UserLiveChannel), which only the account's own
/// connections subscribe while their session still proves it.</summary>
public sealed class LiveSecurityEvents(LiveRelay relay) : ISecurityEvents
{
    public const string RequestChanged = "accessRequestChanged";
    public const string AccessRevoked = "secretAccessRevoked";

    public Task AccessRequestChangedAsync(IReadOnlyCollection<Guid> userIds, AccessRequestChanged change)
        => Task.WhenAll(userIds.Distinct().Select(id => relay.PublishAsync(UserLiveChannel.Name(id), RequestChanged, change)));

    public Task SecretAccessRevokedAsync(Guid userId, SecretAccessRevoked revoked)
        => relay.PublishAsync(UserLiveChannel.Name(userId), AccessRevoked, revoked);
}
