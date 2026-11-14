namespace Atlas.Server.Features.Vaults.Domain;

/// <summary>Where KDBX files live. History in VaultLocationRevision, written by VaultLocationHistory.</summary>
public class VaultLocation
{
    /// <summary>A folder Atlas derives from its data directory (VaultFolders); Root stays empty.</summary>
    public const string ManagedKind = "managed";
    public const string LocalKind = "local";
    public const string SmbKind = "smb";

    public virtual Guid Id { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual string Kind { get; set; } = LocalKind;
    public virtual string Root { get; set; } = "";
    public virtual string Username { get; set; } = "";
    public virtual string Domain { get; set; } = "";
    /// <summary>Data Protection payload of the SMB password (VaultProtection); null for local folders.</summary>
    public virtual string? ProtectedPassword { get; set; }
    public virtual DateTime? ArchivedAt { get; set; }

    public override string ToString() => $"Vault location {Id:N}";
}
