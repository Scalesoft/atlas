using NHibernate;
using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Auth.Persistence;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Reauth;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Commands;

/// <summary>
/// Fresh re-authentication (owner decision 27. 9.). A challenge binds the caller, the SHA-256 of its security stamp, its Atlas
/// session, the operation and the target, with IssuedAt and ExpiresAt from the database clock. It is verified once: by the local
/// password (the endpoint applies the builtin login throttle first) or by the provider callback, which requires the same cookie
/// session and stamp, a current linked identity (issuer and subject; never signup or linking) and an id_token auth_time no older than
/// IssuedAt minus <see cref="AuthTimeSkew"/>. A missing or stale auth_time marks the provider unsupported. Failures are recorded
/// and returned, not thrown, so they survive the transaction; nothing here signs in, releases or changes anything else.
/// </summary>
public sealed class ReauthHandler
{
    /// <summary>auth_time has whole seconds and the provider its own clock.</summary>
    public static readonly TimeSpan AuthTimeSkew = TimeSpan.FromSeconds(30);
    private const int PasswordMaxLength = 256;

    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly IAtlasLocks m_locks;
    private readonly FreshAuthMethods m_methods;
    private readonly ReauthProofs m_proofs;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly AtlasOidcOptions m_options;

    public ReauthHandler(ISession session, IContentAccess content, IAtlasLocks locks, FreshAuthMethods methods, ReauthProofs proofs,
        IPasswordHasher<UserAccount> hasher, AtlasOidcOptions options)
    {
        m_session = session;
        m_content = content;
        m_locks = locks;
        m_methods = methods;
        m_proofs = proofs;
        m_hasher = hasher;
        m_options = options;
    }

    public async Task<ReauthStarted> Handle(StartReauth command, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var sessionId = m_proofs.SessionId ?? throw new ApiException(401, "Pro ověření se znovu přihlaste.");
        var (operation, target) = Target(command);
        FreshAuthMethod? method;
        if (operation == ReauthChallenge.SelfTestOperation)
        {
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
            {
                throw new ApiException(403, "Ověření poskytovatele spouští správce wiki.");
            }
            method = await m_methods.ChooseAsync(actor, true, ct, command.ProviderId);
            if (method is null)
            {
                throw new ApiException(409, "Váš účet není propojený s tímto poskytovatelem přihlášení, takže ho nelze ověřit.");
            }
        }
        else
        {
            method = await m_methods.ChooseAsync(actor, false, ct);
            if (method is null || method.Support == FreshAuthSupport.Unsupported)
            {
                throw ReauthRefusal.Unsupported();
            }
        }
        var now = await DatabaseClock.NowAsync(m_session, ct);
        var challenge = new ReauthChallenge
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            SecurityStampHash = ReauthChallenge.HashStamp(actor.SecurityStamp),
            SessionId = sessionId,
            Operation = operation,
            TargetKind = target.Kind,
            TargetId = target.Id,
            Method = method.Method,
            ProviderId = method.ProviderId,
            IssuedAt = now,
            ExpiresAt = now + ReauthChallenge.Lifetime
        };
        await m_session.SaveAsync(challenge, ct);
        return new ReauthStarted(challenge.Id, actor.Id, actor.SecurityStamp, sessionId, challenge.Method, challenge.ProviderId, challenge.ExpiresAt);
    }

    public async Task<ReauthOutcome> Handle(VerifyReauthPassword command, CancellationToken ct)
    {
        var (actor, challenge, now) = await PendingAsync(command.ChallengeId, ReauthChallenge.PasswordMethod, ct);
        if (challenge is null)
        {
            return new ReauthOutcome(false, "missing");
        }
        var credential = m_options.BuiltinEnabled ? await m_session.GetAsync<BuiltinCredential>(actor.Id, ct) : null;
        if (string.IsNullOrEmpty(credential?.PasswordHash) || string.IsNullOrEmpty(command.Password) || command.Password.Length > PasswordMaxLength
            || m_hasher.VerifyHashedPassword(actor, credential.PasswordHash, command.Password) == PasswordVerificationResult.Failed)
        {
            challenge.FailedAt = now;
            challenge.Failure = "password";
            return new ReauthOutcome(false, "password");
        }
        challenge.VerifiedAt = now;
        return new ReauthOutcome(true, null);
    }

    public async Task<ReauthOutcome> Handle(CompleteOidcReauth command, CancellationToken ct)
    {
        var (actor, challenge, now) = await PendingAsync(command.ChallengeId, ReauthChallenge.OidcMethod, ct);
        if (challenge is null || challenge.ProviderId != command.ProviderId)
        {
            return new ReauthOutcome(false, "missing");
        }
        var linked = await m_session.QueryOver<ExternalIdentity>()
            .Where(i => i.Issuer == command.Issuer && i.Subject == command.Subject && i.UserId == actor.Id && i.ActiveTo == null)
            .RowCountAsync(ct);
        if (linked == 0)
        {
            return Fail(challenge, now, "identity");
        }
        challenge.AuthTime = command.AuthTime;
        var fresh = command.AuthTime is { } authTime && authTime >= challenge.IssuedAt - AuthTimeSkew;
        await RecordProviderAsync(command, actor, command.AuthTime is not null, fresh, challenge.Operation == ReauthChallenge.SelfTestOperation, now, ct);
        if (!fresh)
        {
            return Fail(challenge, now, command.AuthTime is null ? "auth_time_missing" : "auth_time_stale");
        }
        challenge.VerifiedAt = now;
        return new ReauthOutcome(true, null);
    }

    /// <summary>The caller's own pending challenge of this method, FOR UPDATE; null when there is none (unknown, another account,
    /// session or stamp, already verified, failed or expired).</summary>
    private async Task<(UserAccount Actor, ReauthChallenge? Challenge, DateTime Now)> PendingAsync(Guid id, string method, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var challenge = await m_session.GetAsync<ReauthChallenge>(id, LockMode.Upgrade, ct);
        var now = await DatabaseClock.NowAsync(m_session, ct);
        if (challenge is null || challenge.Method != method || !challenge.BelongsTo(actor, m_proofs.SessionId) || !challenge.IsPending(now))
        {
            return (actor, null, now);
        }
        return (actor, challenge, now);
    }

    private static ReauthOutcome Fail(ReauthChallenge challenge, DateTime now, string failure)
    {
        challenge.FailedAt = now;
        challenge.Failure = failure;
        return new ReauthOutcome(false, failure);
    }

    /// <summary>The provider's latest fresh-auth evidence, serialized per provider.</summary>
    private async Task RecordProviderAsync(CompleteOidcReauth command, UserAccount actor, bool present, bool fresh, bool selfTest, DateTime now,
        CancellationToken ct)
    {
        await m_locks.AcquireAdvisoryAsync("oidc-fresh-auth\n" + command.ProviderId, ct);
        var status = await m_session.GetAsync<OidcFreshAuthStatus>(command.ProviderId, ct);
        var created = status is null;
        status ??= new OidcFreshAuthStatus { ProviderId = command.ProviderId };
        status.Issuer = command.Issuer;
        status.Verdict = fresh ? OidcFreshAuthStatus.SupportedVerdict : OidcFreshAuthStatus.UnsupportedVerdict;
        status.AuthTimePresent = present;
        status.AuthTimeFresh = fresh;
        status.SelfTest = selfTest;
        status.CheckedAt = now;
        status.CheckedBy = actor.Id;
        if (created)
        {
            await m_session.SaveAsync(status, ct);
        }
    }

    private static (string Operation, ReauthTarget Target) Target(StartReauth command)
    {
        switch (command.Operation)
        {
            case ReauthChallenge.SecurityOperation when command.VaultId is null && command.SecretId is null:
                return (ReauthChallenge.SecurityOperation, ReauthTarget.Instance);
            case ReauthChallenge.SecurityOperation when command.VaultId is { } vault && command.SecretId is null:
                return (ReauthChallenge.SecurityOperation, ReauthTarget.Vault(vault));
            case ReauthChallenge.SecurityOperation when command.VaultId is null && command.SecretId is { } secret:
                return (ReauthChallenge.SecurityOperation, ReauthTarget.Secret(secret));
            case ReauthChallenge.RevealOperation or ReauthChallenge.CopyOperation when command.VaultId is null && command.SecretId is { } secret:
                return (command.Operation, ReauthTarget.Secret(secret));
            case ReauthChallenge.SelfTestOperation when command.VaultId is null && command.SecretId is null:
                return (ReauthChallenge.SelfTestOperation, ReauthTarget.None);
            default:
                throw new ApiException(400, "Neplatné ověření: operace security (bez cíle, s trezorem nebo heslem), reveal nebo copy (s heslem).");
        }
    }
}
