namespace Atlas.Server.Features.Security.Domain;

/// <summary>One level of the inheritance chain: its explicit mode, or null when it inherits.</summary>
public sealed record ProtectionLevel(string Level, Guid? TargetId, ProtectionMode? Mode)
{
    public const string InstanceLevel = "instance";
    public const string VaultLevel = "vault";
    public const string SecretLevel = "secret";
}
