namespace Atlas.Server.Features.Vaults.Requests;

/// <summary>
/// A request of one account for Čte on a whole vault (SecretId null) or one of its secrets, with a short reason. Changes exactly
/// once, from pending to approved (GrantId: the grant it created or the current grant that covered it), rejected or cancelled;
/// the database refuses anything else and audits every transition with its actor. A pending request grants nothing.
/// </summary>
public class VaultAccessRequest
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const int TextMaxLength = 500;

    public virtual Guid Id { get; set; }
    public virtual Guid VaultId { get; set; }
    public virtual Guid? SecretId { get; set; }
    public virtual Guid VaultSpaceId { get; set; }
    public virtual Guid RequesterId { get; set; }
    /// <summary>The requested VaultAccessLevel; only Read (20) can be requested.</summary>
    public virtual int Level { get; set; }
    public virtual string Reason { get; set; } = "";
    public virtual string State { get; set; } = Pending;
    public virtual DateTime CreatedAt { get; set; }
    public virtual Guid? DecidedBy { get; set; }
    public virtual DateTime? DecidedAt { get; set; }
    public virtual string? DecisionNote { get; set; }
    public virtual Guid? GrantId { get; set; }
    public virtual DateTime? GrantExpiresAt { get; set; }

    public override string ToString() => $"Vault access request {Id:N} ({State})";
}
