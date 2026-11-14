namespace Atlas.Server.Features.Vaults.Domain;

/// <summary>Which values of a KeePass entry are non-empty (column VaultSecret.AvailableFields).</summary>
[Flags]
public enum VaultSecretFields : short
{
    None = 0,
    UserName = 1,
    Password = 2,
    Url = 4,
    Notes = 8,
}
