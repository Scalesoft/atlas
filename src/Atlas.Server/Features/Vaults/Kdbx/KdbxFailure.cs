namespace Atlas.Server.Features.Vaults.Kdbx;

public enum KdbxFailure
{
    /// <summary>Not a KeePass file, truncated, or its authenticated content does not verify.</summary>
    Corrupt,

    /// <summary>The supplied password does not open the file.</summary>
    WrongKey,

    /// <summary>A stored password that used to open the file no longer does; the master key was changed.</summary>
    KeyChanged,

    /// <summary>A valid file in a format or with a protection Atlas does not read (KDBX 3.1, key file, Twofish).</summary>
    Unsupported,

    /// <summary>The file exceeds a configured <see cref="VaultOptions"/> limit.</summary>
    LimitExceeded,
}
