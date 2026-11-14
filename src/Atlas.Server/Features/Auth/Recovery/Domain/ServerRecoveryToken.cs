namespace Atlas.Server.Features.Auth.Recovery.Domain;

/// <summary>A one-shot capability that restores local access for one existing wiki administrator.</summary>
public class ServerRecoveryToken
{
    public virtual Guid Id { get; set; }

    public virtual Guid UserId { get; set; }

    public virtual byte[] TokenHash { get; set; } = [];

    public virtual DateTime IssuedAt { get; set; }

    public virtual DateTime ExpiresAt { get; set; }

    public virtual DateTime? ConsumedAt { get; set; }
}
