namespace Atlas.Server.Features.Auth.Recovery.Domain;

/// <summary>An activation or reset capability issued by administration; recovery consumes the pending one. Purpose is fixed at issue:
/// <see cref="InvitationPurpose"/> for an account that has never set a password (seven days), <see cref="ResetPurpose"/> otherwise
/// (thirty minutes).</summary>
public class BuiltinAccountToken
{
    public const string InvitationPurpose = "invitation";
    public const string ResetPurpose = "reset";

    public virtual Guid Id { get; set; }

    public virtual Guid UserId { get; set; }

    public virtual byte[] TokenHash { get; set; } = [];

    public virtual string Purpose { get; set; } = ResetPurpose;

    public virtual DateTime ExpiresAt { get; set; }

    public virtual DateTime? ConsumedAt { get; set; }
}
