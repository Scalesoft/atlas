namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// The latest fresh-authentication evidence of one OIDC provider registration, from the WikiAdmin self-test or a real
/// re-authentication: supported after a validated id_token carried an auth_time no older than its challenge, unsupported when
/// auth_time was missing or stale. No row, or a row for another issuer than the configured one, is unknown. Only derived booleans
/// and times are kept; the id_token itself never is.
/// </summary>
public class OidcFreshAuthStatus
{
    public const string SupportedVerdict = "supported";
    public const string UnsupportedVerdict = "unsupported";

    public virtual string ProviderId { get; set; } = "";
    public virtual string Issuer { get; set; } = "";
    public virtual string Verdict { get; set; } = UnsupportedVerdict;
    public virtual bool AuthTimePresent { get; set; }
    public virtual bool AuthTimeFresh { get; set; }
    public virtual bool SelfTest { get; set; }
    public virtual DateTime CheckedAt { get; set; }
    public virtual Guid? CheckedBy { get; set; }
}
