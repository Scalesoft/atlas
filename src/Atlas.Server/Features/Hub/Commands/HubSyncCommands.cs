using Atlas.Server.Features.Hub.Directory;

namespace Atlas.Server.Features.Hub.Commands;

public sealed record ReserveHubSync(Guid ExternalIdentityId, bool Force = false);
public sealed record HubSyncAttempt(Guid ExternalIdentityId, Guid UserId, Guid ConnectionId, Guid RevisionId,
    long Generation, DateTime StartedAt, string ExternalUserId, string BaseUrl, string ProtectedToken)
{
    public override string ToString() => $"HubSyncAttempt {{ ExternalIdentityId = {ExternalIdentityId}, Generation = {Generation}, Token = [REDACTED] }}";
}
public sealed record ApplyHubSync(HubSyncAttempt Attempt, HubUserObservation? Observation, string? ErrorCode = null, TimeSpan? RetryAfter = null);
public sealed record HubSyncResult(bool Applied, string Status, int Added, int Removed);

