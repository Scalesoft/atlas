namespace Atlas.Server.Features.Vaults.Domain;

/// <summary>One KeePass file of a space. The master password is stored only as a Data Protection payload.</summary>
public class Vault
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual Guid LocationId { get; set; }
    public virtual string FileName { get; set; } = "";
    public virtual string? ProtectedKdbxPassword { get; set; }
    /// <summary>One of <see cref="VaultStates"/>.</summary>
    public virtual string State { get; set; } = VaultStates.Locked;
    public virtual string? LastFailure { get; set; }
    /// <summary>SHA-256 of the encrypted file last read successfully; never a hash of a password or key.</summary>
    public virtual byte[]? LastSnapshotSha256 { get; set; }
    public virtual DateTime? LastRefreshAt { get; set; }
    public virtual Guid CreatedBy { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual DateTime? ArchivedAt { get; set; }

    public override string ToString() => $"Vault {Id:N}";
}
