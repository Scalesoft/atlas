namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// When a value release needs fresh re-authentication (owner decision 27. 9.): Never; OutsideTrustedNetworks, unless the verified
/// client address is in an active trusted network; Always, wherever the client is. A trusted network never waives Always, the
/// access level or the audit.
/// </summary>
public enum ProtectionMode
{
    Never,
    OutsideTrustedNetworks,
    Always
}

/// <summary>The API and database names of <see cref="ProtectionMode"/>: never, outsideTrustedNetworks, always.</summary>
public static class ProtectionModes
{
    public const string NeverName = "never";
    public const string OutsideTrustedNetworksName = "outsideTrustedNetworks";
    public const string AlwaysName = "always";

    public static string Name(this ProtectionMode mode) => mode switch
    {
        ProtectionMode.Never => NeverName,
        ProtectionMode.OutsideTrustedNetworks => OutsideTrustedNetworksName,
        _ => AlwaysName
    };

    /// <summary>A mode by name; null for anything else.</summary>
    public static ProtectionMode? Parse(string? name) => name switch
    {
        NeverName => ProtectionMode.Never,
        OutsideTrustedNetworksName => ProtectionMode.OutsideTrustedNetworks,
        AlwaysName => ProtectionMode.Always,
        _ => null
    };

    /// <summary>A stored mode; an unknown value fails closed to Always.</summary>
    public static ProtectionMode FromStored(string stored) => Parse(stored) ?? ProtectionMode.Always;
}
