namespace Atlas.Server.Features.Security.Contracts;

/// <summary>POST /api/security/reauth. Operation security (a security change: no target for the global configuration, VaultId or
/// SecretId for that vault or secret) or reveal (SecretId, the value release of V5). ReturnUrl: the local path the provider
/// callback returns to.</summary>
public sealed record StartReauthRequest(string? Operation, Guid? VaultId, Guid? SecretId, string? ReturnUrl);

/// <summary>POST /api/security/reauth/self-test (WikiAdmin): ProviderId defaults to the provider of the current sign-in.</summary>
public sealed record StartSelfTestRequest(string? ProviderId, string? ReturnUrl);

/// <summary>A started challenge: Method password (then POST …/{id}/password) or oidc (open RedirectUrl; the provider returns to
/// ReturnUrl and GET …/{id} tells the outcome). The proof goes into the X-Atlas-Reauth header of the change it authorizes.</summary>
public sealed record ReauthStartView(Guid Id, string Method, string? ProviderId, string? ProviderName, DateTime ExpiresAt, string? RedirectUrl);

/// <summary>POST /api/security/reauth/{id}/password.</summary>
public sealed record ReauthPasswordRequest(string? Password)
{
    public override string ToString() => "ReauthPasswordRequest { sensitive values redacted }";
}

/// <summary>A challenge of the caller's own session. State: pending, verified (proof usable until ProofExpiresAt), consumed, failed
/// (provider verification refused) or expired. Failure: password, identity, session, auth_time_missing or auth_time_stale.
/// WindowExpiresAt: once verified (not a self-test), until when further security changes of this session need no new proof (ten
/// minutes after the verification; a stamp rotation or sign-out ends it earlier); never for a value release.</summary>
public sealed record ReauthStatusView(Guid Id, string Operation, string TargetKind, Guid? TargetId, string Method, string State,
    string? Failure, DateTime ExpiresAt, DateTime? ProofExpiresAt, DateTime? WindowExpiresAt);

/// <summary>GET /api/security/reauth/self-test, per configured provider: Verdict supported, unsupported or unknown (never checked or
/// issuer changed), whether the last id_token carried auth_time and whether it was fresh, when, and whether a self-test (not a real
/// re-authentication) recorded it. Whether the provider really showed its login form is not knowable on the server.</summary>
public sealed record FreshAuthProviderView(string ProviderId, string DisplayName, string Verdict, bool? AuthTimePresent,
    bool? AuthTimeFresh, DateTime? CheckedAt, bool? SelfTest);
