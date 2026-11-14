namespace Atlas.Server.Features.Hub.Domain;

public class HubIdentityState
{
    public virtual Guid ExternalIdentityId { get; set; }

    public virtual Guid UserId { get; set; }

    public virtual Guid ConnectionId { get; set; }

    public virtual string ExternalUserId { get; set; } = "";

    public virtual DateTime? SnapshotStartedAt { get; set; }

    public virtual DateTime? VerifiedUntil { get; set; }

    public virtual DateTime? LastSuccessAt { get; set; }

    public virtual DateTime? LastAttemptAt { get; set; }

    public virtual HubUpstreamAccountState UpstreamAccountState { get; set; }

    public virtual long Generation { get; set; }

    public virtual string? LastErrorCode { get; set; }

    public virtual DateTime? NextAttemptAt { get; set; }

    public virtual int FailureCount { get; set; }
}

