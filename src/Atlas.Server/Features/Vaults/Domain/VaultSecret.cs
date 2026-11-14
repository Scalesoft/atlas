namespace Atlas.Server.Features.Vaults.Domain;

/// <summary>
/// The stable Atlas secret identity (<see cref="Id"/>, used in documents) of one KeePass entry. Metadata only:
/// values stay in the KDBX file.
/// </summary>
public class VaultSecret
{
    public const int TitleMaxLength = 500;
    public const int GroupPathMaxLength = 1000;

    public virtual Guid Id { get; set; }
    public virtual Guid VaultId { get; set; }
    public virtual Guid EntryUuid { get; set; }
    public virtual string Title { get; set; } = "";
    public virtual string GroupPath { get; set; } = "";
    public virtual VaultSecretFields AvailableFields { get; set; }
    public virtual DateTime FirstSeenAt { get; set; }
    public virtual DateTime? RemovedAt { get; set; }

    public override string ToString() => $"Vault secret {Id:N}";
}
