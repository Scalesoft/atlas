namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// A named trusted address rule: a network (LAN, VPN or other) that waives fresh re-authentication in the "outside trusted networks"
/// mode, or a reverse proxy whose forwarded headers Atlas accepts. Range is a normalized CIDR (<see cref="Networks.AddressRange"/>).
/// Removing archives it. History in TrustedAddressRevision, written by TR_TrustedAddress_History with the configuration version.
/// </summary>
public class TrustedAddress
{
    public const string NetworkPurpose = "network";
    public const string ProxyPurpose = "proxy";

    public virtual Guid Id { get; set; }
    public virtual string Purpose { get; set; } = NetworkPurpose;
    public virtual string Name { get; set; } = "";
    /// <summary>lan, vpn or other for a network; null for a proxy.</summary>
    public virtual string? Kind { get; set; }
    public virtual string Range { get; set; } = "";
    public virtual bool IsActive { get; set; }
    public virtual DateTime? ArchivedAt { get; set; }

    public override string ToString() => $"Trusted {Purpose} {Id:N}";
}
