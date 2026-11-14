namespace Atlas.Server.Platform.Identity;

/// <summary>Read-only view of one temporal item grant to a user or a group. ActiveTo NULL is the current interval;
/// StartAt/ExpiresAt are business validity. Exactly one target: a page (Role uses SpaceGrant numbering: 1 DocumentReader,
/// 2 DocumentWriter, 3 DocumentManager), a private object (ObjectId: 1 read, 2 write; ObjectGrants), a vault (VaultId without SecretId) or a secret (SecretId with its VaultId); a vault or
/// secret grant keeps its <see cref="VaultAccessLevel"/> in Role and the vault's space in VaultSpaceId.</summary>
public class ResourceGrant
{
    public virtual Guid Id { get; set; }
    public virtual Guid? PageId { get; set; }
    public virtual Guid? ObjectId { get; set; }
    public virtual Guid? VaultId { get; set; }
    public virtual Guid? SecretId { get; set; }
    public virtual Guid? VaultSpaceId { get; set; }
    public virtual Guid? UserId { get; set; }
    public virtual Guid? GroupId { get; set; }
    public virtual int Role { get; set; }
    public virtual DateTime? StartAt { get; set; }
    public virtual DateTime? ExpiresAt { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
