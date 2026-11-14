namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// The one row (Id 1) that versions the security configuration. A change of any protection mode, trusted network, trusted proxy or
/// the hop limit takes this row FOR UPDATE and increments Version in the same transaction (atomic activation); an evaluation takes
/// it FOR SHARE and so reads one consistent version. History in SecurityConfigurationRevision (trigger, actor id).
/// </summary>
public class SecurityConfiguration
{
    public const short SingletonId = 1;
    public const int MaxProxyHops = 5;

    public virtual short Id { get; set; }
    public virtual long Version { get; set; }
    /// <summary>How many trusted proxies a request may pass; more makes its client address unclear.</summary>
    public virtual short ProxyHopLimit { get; set; }
    /// <summary>When the explicit Http:TrustedProxies addresses were imported (once); null before the first start on this schema.</summary>
    public virtual DateTime? ProxiesImportedAt { get; set; }
}
