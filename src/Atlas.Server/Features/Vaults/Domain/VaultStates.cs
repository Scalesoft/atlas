namespace Atlas.Server.Features.Vaults.Domain;

/// <summary>Persisted outcome of the last unlock or refresh of a vault (column Vault.State).</summary>
public static class VaultStates
{
    /// <summary>The stored password opened the file. A LastFailure then means the file was temporarily unavailable.</summary>
    public const string Unlocked = "unlocked";

    /// <summary>No working password: the master key changed or the stored password cannot be decrypted.</summary>
    public const string Locked = "locked";

    /// <summary>The file is damaged, unsupported or over a configured limit.</summary>
    public const string Error = "error";
}
