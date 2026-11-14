namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>State of a vault in this process (<see cref="VaultSnapshots"/>).</summary>
public enum VaultRuntimeState
{
    /// <summary>No usable key: no stored password, or it cannot be decrypted. An administrator enters it again.</summary>
    Locked,

    /// <summary>The snapshot matches the file as of the last metadata check.</summary>
    Fresh,

    /// <summary>The location or file cannot be reached. No entries are served until the next successful check.</summary>
    Unavailable,

    /// <summary>The stored password no longer opens the file; the key was dropped and the vault needs an administrator.</summary>
    KeyChanged,

    /// <summary>The file is damaged, unsupported or over a limit.</summary>
    Error,
}
