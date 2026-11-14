namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// Inheritance instance → vault → secret (owner decision 27. 9.): the effective mode is the nearest explicit one, looking from the
/// most specific level up. The instance always has an explicit mode; should it be missing, the chain fails closed to Always.
/// </summary>
public sealed record ProtectionChain(IReadOnlyList<ProtectionLevel> Levels)
{
    /// <summary>The instance, then the vault (when given), then the secret (when given).</summary>
    public static ProtectionChain Of(ProtectionMode? instance, Guid? vaultId = null, ProtectionMode? vault = null, Guid? secretId = null,
        ProtectionMode? secret = null)
    {
        var levels = new List<ProtectionLevel> { new(ProtectionLevel.InstanceLevel, null, instance) };
        if (vaultId is not null)
        {
            levels.Add(new ProtectionLevel(ProtectionLevel.VaultLevel, vaultId, vault));
        }
        if (secretId is not null)
        {
            levels.Add(new ProtectionLevel(ProtectionLevel.SecretLevel, secretId, secret));
        }
        return new ProtectionChain(levels);
    }

    /// <summary>The nearest explicit level; the instance (with Always) when none is explicit.</summary>
    public ProtectionLevel Source => Levels.LastOrDefault(level => level.Mode is not null) ?? Levels[0];

    public ProtectionMode Effective => Source.Mode ?? ProtectionMode.Always;

    /// <summary>The last level, whose own setting a change of this chain edits.</summary>
    public ProtectionLevel Target => Levels[^1];
}
