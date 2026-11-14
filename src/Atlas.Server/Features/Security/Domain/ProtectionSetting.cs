namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// The explicit protection mode of one level: the instance (VaultId and SecretId null), a vault, or a secret of that vault. A level
/// without a current row inherits. Immutable except for closing (ActiveTo); TR_ProtectionSetting_Audit writes protection.set and
/// protection.cleared with the locked actor.
/// </summary>
public class ProtectionSetting
{
    public virtual Guid Id { get; set; }
    public virtual Guid? VaultId { get; set; }
    public virtual Guid? SecretId { get; set; }
    public virtual string Mode { get; set; } = ProtectionModes.AlwaysName;
    /// <summary>The security configuration version this row activated.</summary>
    public virtual long ConfigurationVersion { get; set; }
    public virtual Guid? ChangedBy { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
