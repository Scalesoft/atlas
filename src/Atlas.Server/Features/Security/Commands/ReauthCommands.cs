namespace Atlas.Server.Features.Security.Commands;

/// <summary>Starts a re-authentication challenge for the caller's current session (POST /api/security/reauth and …/self-test).
/// ProviderId only for the self-test.</summary>
public sealed record StartReauth(string? Operation, Guid? VaultId, Guid? SecretId, string? ProviderId = null);

/// <summary>A started challenge; the endpoint turns an oidc one into the provider redirect.</summary>
public sealed record ReauthStarted(Guid Id, Guid UserId, string SecurityStamp, string SessionId, string Method, string? ProviderId,
    DateTime ExpiresAt)
{
    public override string ToString() => $"ReauthStarted {{ Method = {Method} }}";
}

/// <summary>Checks the local password against a pending password challenge of the caller.</summary>
public sealed record VerifyReauthPassword(Guid ChallengeId, string Password)
{
    public override string ToString() => "VerifyReauthPassword { sensitive values redacted }";
}

/// <summary>The provider callback of a reauth challenge: the validated issuer and subject and the id_token auth_time (null when
/// the token carried none). Never signs in.</summary>
public sealed record CompleteOidcReauth(Guid ChallengeId, string ProviderId, string Issuer, string Subject, DateTime? AuthTime)
{
    public override string ToString() => "CompleteOidcReauth { sensitive values redacted }";
}

/// <summary>Verified, or the failure code (password, identity, session, auth_time_missing, auth_time_stale, missing).</summary>
public sealed record ReauthOutcome(bool Verified, string? Failure);

/// <summary>Sign-out: ends the re-authentication window of the caller's session on the server (owner decision 28. 9.).</summary>
public sealed record EndReauthWindow;
