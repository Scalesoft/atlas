using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.Security.Domain;

/// <summary>
/// One fresh re-authentication (owner decision 27. 9.). Bound to the account, its security stamp (SHA-256), the Atlas session, the
/// operation and its target. The challenge must be verified before ExpiresAt (IssuedAt + 5 minutes, database clock); the verified
/// proof is consumed at most once, within <see cref="ProofLifetime"/> of VerifiedAt, by the change or release it proves.
/// A verified challenge also opens the re-authentication window of its session (owner decision 28. 9., "sudo mode"): for
/// <see cref="WindowLifetime"/> from VerifiedAt, further security changes of the same account, session and stamp need no new proof.
/// The window never covers a value release (both protection modes that ask for a fresh sign-in promise one before every release), and
/// a self-test opens none. Operational state: no secret, token or password is stored.
/// </summary>
public class ReauthChallenge
{
    public const string SecurityOperation = "security";
    public const string RevealOperation = "reveal";
    public const string CopyOperation = "copy";
    public const string SelfTestOperation = "selftest";
    public const string PasswordMethod = "password";
    public const string OidcMethod = "oidc";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan WindowLifetime = TimeSpan.FromMinutes(10);

    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual byte[] SecurityStampHash { get; set; } = [];
    public virtual string SessionId { get; set; } = "";
    public virtual string Operation { get; set; } = SecurityOperation;
    /// <summary>instance, vault, secret or none (self-test).</summary>
    public virtual string TargetKind { get; set; } = ReauthTarget.InstanceKind;
    public virtual Guid? TargetId { get; set; }
    public virtual string Method { get; set; } = PasswordMethod;
    public virtual string? ProviderId { get; set; }
    public virtual DateTime IssuedAt { get; set; }
    public virtual DateTime ExpiresAt { get; set; }
    public virtual DateTime? VerifiedAt { get; set; }
    public virtual DateTime? ConsumedAt { get; set; }
    public virtual DateTime? FailedAt { get; set; }
    public virtual string? Failure { get; set; }
    /// <summary>The provider's auth_time claim, when one was present.</summary>
    public virtual DateTime? AuthTime { get; set; }
    /// <summary>When a sign-out of the session ended the window this verification opened.</summary>
    public virtual DateTime? WindowClosedAt { get; set; }

    public static byte[] HashStamp(string stamp) => SHA256.HashData(Encoding.UTF8.GetBytes(stamp));

    /// <summary>The same active account with the same security stamp in the same Atlas session.</summary>
    public virtual bool BelongsTo(UserAccount actor, string? sessionId)
    {
        return UserId == actor.Id && actor.IsActive && sessionId is not null && string.Equals(SessionId, sessionId, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(SecurityStampHash, HashStamp(actor.SecurityStamp));
    }

    /// <summary>Waiting for verification: not verified, not consumed, no terminal provider failure and not expired.</summary>
    public virtual bool IsPending(DateTime now)
    {
        return VerifiedAt is null && ConsumedAt is null && now < ExpiresAt && !(Method == OidcMethod && FailedAt is not null);
    }

    /// <summary>A verified, unconsumed proof for exactly this operation and target, still inside its lifetime.</summary>
    public virtual bool Proves(string operation, ReauthTarget target, DateTime now)
    {
        // now >= verified: a change whose transaction began before the verification committed is not proven by it.
        return VerifiedAt is { } verified && ConsumedAt is null && WindowClosedAt is null && now >= verified && now < verified + ProofLifetime
            && Operation == operation && TargetKind == target.Kind && TargetId == target.Id;
    }

    /// <summary>The end of the security-change window this verification opens for its session; null when it opens none (not verified,
    /// a self-test, which unlocks nothing, or ended by a sign-out).</summary>
    public virtual DateTime? WindowEnd()
    {
        return VerifiedAt is { } verified && Operation != SelfTestOperation && WindowClosedAt is null ? verified + WindowLifetime : null;
    }

    /// <summary>pending, verified, consumed, failed or expired, for GET /api/security/reauth/{id}.</summary>
    public virtual string State(DateTime now)
    {
        if (ConsumedAt is not null)
        {
            return "consumed";
        }
        if (VerifiedAt is { } verified)
        {
            return now < verified + ProofLifetime ? "verified" : "expired";
        }
        if (Method == OidcMethod && FailedAt is not null)
        {
            return "failed";
        }
        return now < ExpiresAt ? "pending" : "expired";
    }

    public override string ToString() => "ReauthChallenge";
}
