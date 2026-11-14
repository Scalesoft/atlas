namespace Atlas.Server.Platform.Identity;

/// <summary>
/// One access level over a vault or a secret, a staircase like the page roles (owner decision 27. 9.): every level includes the
/// lower ones. Discover (Vidí) sees that a secret exists: title, path and field names. Read (Čte) also reveals and copies the value
/// (the release itself is WP4 V5). Manage (Spravuje) also grants and revokes access, changes protection (V4) and reads the audit.
/// Stored in the Role of a vault or secret ResourceGrant. 30 is reserved for Zapisuje (writing values in Atlas), which 1.1 does not
/// have: values are managed in KeePass.
/// </summary>
public enum VaultAccessLevel
{
    None = 0,
    Discover = 10,
    Read = 20,
    Manage = 40
}

/// <summary>Level checks and the API names of <see cref="VaultAccessLevel"/> (discover, read, manage).</summary>
public static class VaultAccessLevels
{
    public static bool CanDiscover(this VaultAccessLevel level) => level >= VaultAccessLevel.Discover;

    public static bool CanRead(this VaultAccessLevel level) => level >= VaultAccessLevel.Read;

    public static bool CanManage(this VaultAccessLevel level) => level >= VaultAccessLevel.Manage;

    /// <summary>A stored Role; an unknown value is no access.</summary>
    public static VaultAccessLevel FromRole(int role) => role switch
    {
        10 => VaultAccessLevel.Discover,
        20 => VaultAccessLevel.Read,
        40 => VaultAccessLevel.Manage,
        _ => VaultAccessLevel.None
    };

    /// <summary>discover, read or manage; null for no access.</summary>
    public static string? Name(this VaultAccessLevel level) => level switch
    {
        VaultAccessLevel.Discover => "discover",
        VaultAccessLevel.Read => "read",
        VaultAccessLevel.Manage => "manage",
        _ => null
    };

    /// <summary>A grantable level by name; null for anything else (no access, writing values, unknown names).</summary>
    public static VaultAccessLevel? Parse(string? name) => name switch
    {
        "discover" => VaultAccessLevel.Discover,
        "read" => VaultAccessLevel.Read,
        "manage" => VaultAccessLevel.Manage,
        _ => null
    };
}
